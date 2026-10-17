using KaraokeFilmMaker.Application.Timeline;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class VideoTimelineServiceTests
{
    private readonly VideoTimelineService _service = new();

    [Fact]
    public void UpdateClip_AllowsGapAndCrop()
    {
        (KaraokeProject project, VideoClip first, _) = CreateProject();

        KaraokeProject result = _service.UpdateClip(
            project, first.Id, ProjectTime.FromSeconds(2), ProjectTime.FromSeconds(1),
            ProjectDuration.FromSeconds(3), isMuted: false);

        VideoClip updated = Assert.Single(result.VideoClips, clip => clip.Id == first.Id);
        Assert.Equal(2, updated.TimelineStart.TotalSeconds);
        Assert.Equal(1, updated.SourceStart.TotalSeconds);
        Assert.Equal(3, updated.Duration.TotalSeconds);
        Assert.False(updated.IsMuted);
    }

    [Fact]
    public void UpdateClip_RejectsOverlap()
    {
        (KaraokeProject project, _, VideoClip second) = CreateProject();

        Assert.Throws<InvalidOperationException>(() => _service.UpdateClip(
            project, second.Id, ProjectTime.FromSeconds(4), ProjectTime.Zero,
            ProjectDuration.FromSeconds(4), isMuted: true));
    }

    [Fact]
    public void UpdateClip_RejectsCropPastAssetEnd()
    {
        (KaraokeProject project, VideoClip first, _) = CreateProject();

        Assert.Throws<ArgumentOutOfRangeException>(() => _service.UpdateClip(
            project, first.Id, ProjectTime.Zero, ProjectTime.FromSeconds(9),
            ProjectDuration.FromSeconds(2), isMuted: true));
    }

    [Fact]
    public void JoinSequentially_UsesRequestedOrderWithoutGaps()
    {
        (KaraokeProject project, VideoClip first, VideoClip second) = CreateProject();

        KaraokeProject result = _service.JoinSequentially(project, [second.Id, first.Id]);

        Assert.Equal(second.Id, result.VideoClips[0].Id);
        Assert.Equal(ProjectTime.Zero, result.VideoClips[0].TimelineStart);
        Assert.Equal(result.VideoClips[0].TimelineEnd, result.VideoClips[1].TimelineStart);
    }

    [Fact]
    public void SingleVideoTrackRejectsMovingToAdditionalTrack()
    {
        (KaraokeProject project, _, VideoClip second) = CreateProject();
        Assert.Throws<ArgumentOutOfRangeException>(() => _service.UpdateClip(project, second.Id, ProjectTime.FromSeconds(1), ProjectTime.Zero,
            second.Duration, true, trackIndex: 1));
        Assert.Throws<InvalidOperationException>(() => _service.UpdateClip(project, second.Id,
            ProjectTime.FromSeconds(1), ProjectTime.Zero, second.Duration, true, trackIndex: 0));
        var merged = _service.UpdateClip(project, second.Id, ProjectTime.FromSeconds(5), ProjectTime.Zero,
            second.Duration, true, trackIndex: 0);
        Assert.All(merged.VideoClips, clip => Assert.Equal(0, clip.TrackIndex));
    }

    [Fact]
    public void VisibleClipRespectsSingleTrackGapsAndExclusiveEnd()
    {
        (KaraokeProject project, VideoClip first, VideoClip second) = CreateProject();
        Assert.Equal(first.Id, VideoTimelineService.VisibleClipAt(project, ProjectTime.FromSeconds(2))!.Id);
        Assert.Equal(second.Id, VideoTimelineService.VisibleClipAt(project, ProjectTime.FromSeconds(6))!.Id);
        Assert.Null(VideoTimelineService.VisibleClipAt(project, ProjectTime.FromSeconds(5)));
        Assert.Null(VideoTimelineService.VisibleClipAt(project, ProjectTime.FromSeconds(10)));
    }

    [Fact]
    public void SplitPreservesTrackSourceOffsetAndMute()
    {
        (KaraokeProject project, VideoClip first, VideoClip second) = CreateProject();
        var moved = _service.UpdateClip(project, second.Id, ProjectTime.FromSeconds(6), ProjectTime.FromSeconds(2),
            ProjectDuration.FromSeconds(4), false, 0);
        var split = _service.SplitClip(moved, second.Id, ProjectTime.FromSeconds(7.5));
        var clips = split.VideoClips.Where(clip => clip.Id != first.Id).OrderBy(clip => clip.TimelineStart).ToArray();
        Assert.Equal(2, clips.Length);
        Assert.Equal(1.5, clips[0].Duration.TotalSeconds);
        Assert.Equal(3.5, clips[1].SourceStart.TotalSeconds);
        Assert.Equal(clips[0].TimelineEnd, clips[1].TimelineStart);
        Assert.All(clips, clip => Assert.False(clip.IsMuted));
        Assert.Empty(KaraokeFilmMaker.Domain.Validation.ProjectValidator.Validate(split));
    }

    [Fact]
    public void JoinSingleTrackKeepsAllSourceRanges()
    {
        (KaraokeProject project, VideoClip first, VideoClip second) = CreateProject();
        var joined = _service.JoinSequentially(project, [first.Id, second.Id]);
        Assert.All(joined.VideoClips, clip => Assert.Equal(0, clip.TrackIndex));
        Assert.Equal(5, joined.VideoClips[1].TimelineStart.TotalSeconds);
        Assert.Equal(second.Duration, joined.VideoClips[1].Duration);
    }

    [Fact]
    public void ProjectHasExactlyOneVideoTrackAndInvalidTargetsAreRejected()
    {
        (KaraokeProject project, VideoClip first, _) = CreateProject();
        Assert.Equal(1, project.VideoTrackCount);
        Assert.NotEmpty(KaraokeFilmMaker.Domain.Validation.ProjectValidator.Validate(project with { VideoTrackCount = 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => _service.UpdateClip(project, first.Id, first.TimelineStart,
            first.SourceStart, first.Duration, true, 2));
        Assert.NotEmpty(KaraokeFilmMaker.Domain.Validation.ProjectValidator.Validate(project with
        { VideoClips = [first with { TrackIndex = -1 }] }));
    }

    private static (KaraokeProject Project, VideoClip First, VideoClip Second) CreateProject()
    {
        MediaAsset asset = new(
            Guid.NewGuid(), "video.mp4", "media/video/video.mp4", MediaKind.Video,
            ProjectDuration.FromSeconds(10));
        VideoClip first = new(
            Guid.NewGuid(), asset.Id, ProjectTime.Zero, ProjectTime.Zero,
            ProjectDuration.FromSeconds(5), true);
        VideoClip second = new(
            Guid.NewGuid(), asset.Id, ProjectTime.FromSeconds(6), ProjectTime.Zero,
            ProjectDuration.FromSeconds(4), true);
        KaraokeProject project = KaraokeProject.Create("Test", DateTimeOffset.UtcNow) with
        {
            MediaAssets = [asset],
            VideoClips = [first, second],
        };
        return (project, first, second);
    }
}
