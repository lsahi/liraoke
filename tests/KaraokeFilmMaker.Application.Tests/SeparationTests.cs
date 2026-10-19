using KaraokeFilmMaker.Application.Separation;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class SeparationTests
{
    [Theory]
    [InlineData(6, 1, 12080, false)] // Pascal
    [InlineData(7, 0, 12080, false)] // Volta deliberately outside first-release support.
    [InlineData(7, 5, 12080, true)]
    [InlineData(8, 6, 12080, true)]
    [InlineData(8, 9, 12080, true)]
    [InlineData(12, 0, 12080, true)]
    [InlineData(12, 0, 12070, false)]
    [InlineData(13, 0, 13000, false)] // Do not guess support for future architectures.
    public void GpuCompatibilityIsConservative(int major, int minor, int driver, bool supported)
    {
        var actual = GpuSupportPolicy.Evaluate([new(0, "Test NVIDIA", major, minor, driver)]);
        Assert.Equal(supported, actual.Supported);
        Assert.NotEmpty(actual.Reason);
    }

    [Fact]
    public void NonNvidiaAndMissingDriverRemainDisabled() => Assert.False(GpuSupportPolicy.Evaluate([]).Supported);

    [Fact]
    public void SelectsSupportedGpuInsteadOfFirstOldDevice()
    {
        var result = GpuSupportPolicy.Evaluate([new(0, "Old", 6, 1, 12080), new(1, "New", 12, 0, 12080)]);
        Assert.True(result.Supported); Assert.Equal(1, result.Device!.Index);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddingSeparatedAudioPreservesAllLyricsAndClockConfiguration(bool use)
    {
        var source = new MediaAsset(Guid.NewGuid(), "Source", "media/video/a.mp4", MediaKind.Video, ProjectDuration.FromSeconds(20));
        var other = new MediaAsset(Guid.NewGuid(), "Other", "media/video/b.mp4", MediaKind.Video, ProjectDuration.FromSeconds(20));
        var unit = new LyricUnit(Guid.NewGuid(), [new TextRange(0, 1)], "歌", null,
            new(ProjectTime.FromSeconds(2), ProjectTime.FromSeconds(3)), ProjectTime.FromSeconds(2.01), true);
        var lyrics = new LyricDocument(LyricLanguage.Chinese, [new(Guid.NewGuid(), "歌", AnnotationType.None, null, null,
            new(ProjectTime.FromSeconds(2), ProjectTime.FromSeconds(3)), [unit], ReviewState.Confirmed, ReadingSource.Manual)]);
        var original = KaraokeProject.Create("Test", DateTimeOffset.UtcNow) with
        {
            MediaAssets = [source, other], MasterAudioAssetId = source.Id, Lyrics = lyrics,
            WorkArea = new(ProjectTime.FromSeconds(1), ProjectTime.FromSeconds(19)), AudioVisualizationEnabled = true,
            VideoClips = [new(Guid.NewGuid(), source.Id, ProjectTime.Zero, ProjectTime.Zero, source.Duration, false),
                new(Guid.NewGuid(), other.Id, ProjectTime.FromSeconds(20), ProjectTime.Zero, other.Duration, false)],
        };
        var added = new MediaAsset(Guid.NewGuid(), "伴奏", "media/audio/new.wav", MediaKind.Audio, source.Duration);
        var imported = original with { MediaAssets = [source, other, added], MasterAudioAssetId = added.Id, WorkArea = null };
        var result = SeparatedAudioProject.Apply(original, imported, source.Id, use);
        Assert.Same(lyrics, result.Lyrics);
        Assert.Equal(original.WorkArea, result.WorkArea);
        Assert.Equal(original.Metronome, result.Metronome);
        Assert.Equal(original.AudioVisualizationEnabled, result.AudioVisualizationEnabled);
        Assert.Equal(original.SubtitleStyle, result.SubtitleStyle);
        Assert.Contains(source, result.MediaAssets);
        Assert.Equal(use ? added.Id : source.Id, result.MasterAudioAssetId);
        Assert.Equal(use, result.VideoClips[0].IsMuted);
        Assert.False(result.VideoClips[1].IsMuted);
    }
}
