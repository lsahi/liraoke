using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Validation;

namespace KaraokeFilmMaker.Domain.Tests;

public sealed class WorkAreaTests
{
    [Fact]
    public void DefaultIsEntireSongAndExplicitRangeDoesNotModifySource()
    {
        var song = new MediaAsset(Guid.NewGuid(), "song", "media/song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(10));
        var project = KaraokeProject.Create("裁切", DateTimeOffset.UtcNow) with { MasterAudioAssetId = song.Id, MediaAssets = [song] };
        Assert.Equal(10, project.GetWorkArea().Duration.TotalSeconds);
        var cropped = project with { WorkArea = new(ProjectTime.FromSeconds(1.25), ProjectTime.FromSeconds(8.75)) };
        Assert.Empty(ProjectValidator.Validate(cropped));
        Assert.Equal(7.5, cropped.GetWorkArea().Duration.TotalSeconds);
        Assert.Equal(project.MediaAssets, cropped.MediaAssets);
        Assert.Equal(project.Lyrics, cropped.Lyrics);
        Assert.Equal(project.VideoClips, cropped.VideoClips);
        Assert.Equal(project.GetWorkArea(), (cropped with { WorkArea = null }).GetWorkArea());
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 1, true)]
    [InlineData(0, 11, true)]
    [InlineData(0, 1, false)]
    public void InvalidOrOrphanedRangeIsRejected(double start, double end, bool hasSong)
    {
        var song = new MediaAsset(Guid.NewGuid(), "song", "song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(10));
        var project = KaraokeProject.Create("裁切", DateTimeOffset.UtcNow) with
        {
            MasterAudioAssetId = hasSong ? song.Id : null, MediaAssets = [song],
            WorkArea = new(ProjectTime.FromSeconds(start), ProjectTime.FromSeconds(end)),
        };
        Assert.Contains(ProjectValidator.Validate(project), message => message.Contains("裁切"));
    }
}
