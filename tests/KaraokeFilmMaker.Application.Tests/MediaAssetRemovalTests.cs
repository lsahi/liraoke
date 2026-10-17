using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Application.Media;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class MediaAssetRemovalTests
{
    [Fact]
    public void UnusedAssetDoesNotRequireConfirmationOrChangeLyrics()
    {
        var project = Create();
        var unused = project.MediaAssets[1];
        Assert.False(MediaAssetRemoval.Inspect(project, unused.Id).RequiresConfirmation);
        var removed = MediaAssetRemoval.Remove(project, unused.Id);
        Assert.Single(removed.MediaAssets);
        Assert.Equal(project.MasterAudioAssetId, removed.MasterAudioAssetId);
        Assert.Same(project.Lyrics, removed.Lyrics);
        Assert.Equal(project.VideoClips, removed.VideoClips);
    }

    [Fact]
    public void RemovingSongVideoRequiresConfirmationAndRemovesAllReferencesButNeverTimings()
    {
        var project = Create();
        var impact = MediaAssetRemoval.Inspect(project, project.MediaAssets[0].Id);
        Assert.True(impact.IsMasterAudio);
        Assert.Equal(2, impact.VideoClipCount);
        Assert.Throws<InvalidOperationException>(() => MediaAssetRemoval.Remove(project, impact.Asset.Id));
        Assert.Equal(2, project.VideoClips.Count);
        var removed = MediaAssetRemoval.Remove(project, impact.Asset.Id, true);
        Assert.Null(removed.MasterAudioAssetId);
        Assert.Empty(removed.VideoClips);
        Assert.Single(removed.MediaAssets);
        Assert.Same(project.Lyrics, removed.Lyrics);
    }

    [Fact]
    public void MissingAssetIsRejected() =>
        Assert.Throws<ArgumentException>(() => MediaAssetRemoval.Remove(Create(), Guid.NewGuid(), true));

    private static KaraokeProject Create()
    {
        var video = new MediaAsset(Guid.NewGuid(), "song.mp4", "media/video/song.mp4", MediaKind.Video, ProjectDuration.FromSeconds(10));
        var unused = new MediaAsset(Guid.NewGuid(), "unused.mp3", "media/audio/unused.mp3", MediaKind.Audio, ProjectDuration.FromSeconds(10));
        return KaraokeProject.Create("Remove", DateTimeOffset.UtcNow) with
        {
            MediaAssets = [video, unused], MasterAudioAssetId = video.Id,
            Lyrics = new LyricImportService().Parse("[00:01]歌词\n[00:05]保留", ".lrc", LyricLanguage.Chinese),
            VideoClips = [new(Guid.NewGuid(), video.Id, ProjectTime.Zero, ProjectTime.Zero, ProjectDuration.FromSeconds(2), false),
                new(Guid.NewGuid(), video.Id, ProjectTime.FromSeconds(5), ProjectTime.FromSeconds(2), ProjectDuration.FromSeconds(3), true)],
        };
    }
}
