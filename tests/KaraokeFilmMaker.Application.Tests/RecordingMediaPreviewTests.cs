using KaraokeFilmMaker.Application.Timeline;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class RecordingMediaPreviewTests
{
    [Theory]
    [InlineData(MediaKind.Audio, false)]
    [InlineData(MediaKind.Video, true)]
    public void RecordingFollowsSongSourceNotEditedBackground(MediaKind kind, bool visible)
    {
        var source = new MediaAsset(Guid.NewGuid(), "song", "media/song", kind, ProjectDuration.FromSeconds(20));
        var background = new MediaAsset(Guid.NewGuid(), "background", "media/background", MediaKind.Video, ProjectDuration.FromSeconds(10));
        var project = KaraokeProject.Create("test", DateTimeOffset.UtcNow) with
        {
            MediaAssets = [source, background], MasterAudioAssetId = source.Id,
            VideoClips = [new VideoClip(Guid.NewGuid(), background.Id, ProjectTime.Zero, ProjectTime.FromSeconds(2), ProjectDuration.FromSeconds(8), true)],
        };
        var clip = RecordingMediaPreview.At(project, ProjectTime.FromSeconds(5));
        Assert.Equal(visible, clip is not null);
        if (visible) { Assert.Equal(source.Id, clip!.MediaAssetId); Assert.Equal(ProjectTime.Zero, clip.SourceStart); }
        Assert.Equal(background.Id, VideoTimelineService.VisibleClipAt(project, ProjectTime.FromSeconds(5))!.MediaAssetId);
        Assert.Null(RecordingMediaPreview.At(project, ProjectTime.FromSeconds(20)));
        Assert.Null(RecordingMediaPreview.At(project with { MasterAudioAssetId = null }, ProjectTime.Zero));
    }
}
