using KaraokeFilmMaker.Application.Timeline;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;
using KaraokeFilmMaker.Domain.Validation;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class ImageTimelineTests
{
    private readonly VideoTimelineService _service = new();

    [Theory]
    [InlineData(0.1)]
    [InlineData(60)]
    public void ImageDurationIsIndependentOfSourceDuration(double seconds)
    {
        var project = Project();
        var clip = project.VideoClips[0];
        var updated = _service.UpdateClip(project, clip.Id, ProjectTime.FromSeconds(2), ProjectTime.Zero,
            ProjectDuration.FromSeconds(seconds), true);
        Assert.Equal(seconds, updated.VideoClips[0].Duration.TotalSeconds);
        Assert.Empty(ProjectValidator.Validate(updated));
        Assert.Equal(5, VideoTimelineService.DefaultImageDuration.TotalSeconds);
    }

    [Fact]
    public void ImageCannotSeekWithinItsSourceOrHaveZeroDuration()
    {
        var project = Project();
        var clip = project.VideoClips[0];
        Assert.Throws<ArgumentOutOfRangeException>(() => _service.UpdateClip(project, clip.Id, ProjectTime.Zero,
            ProjectTime.FromSeconds(1), clip.Duration, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => _service.UpdateClip(project, clip.Id, ProjectTime.Zero,
            ProjectTime.Zero, ProjectDuration.Zero, true));
        Assert.NotEmpty(ProjectValidator.Validate(project with
            { VideoClips = [clip with { SourceStart = ProjectTime.FromSeconds(1) }] }));
        Assert.NotEmpty(ProjectValidator.Validate(project with
            { VideoClips = [clip with { Duration = ProjectDuration.Zero }] }));
    }

    [Fact]
    public void ImageSplitsAndDuplicatesKeepZeroSourceOffsetsAndCanBeRejoined()
    {
        var project = Project();
        var split = _service.SplitClip(project, project.VideoClips[0].Id, ProjectTime.FromSeconds(2));
        Assert.Equal(2, split.VideoClips.Count);
        Assert.All(split.VideoClips, clip => Assert.Equal(ProjectTime.Zero, clip.SourceStart));
        Assert.Equal(2, split.VideoClips[0].Duration.TotalSeconds);
        Assert.Equal(3, split.VideoClips[1].Duration.TotalSeconds);
        var duplicated = _service.DuplicateClip(split, split.VideoClips[1].Id);
        var joined = _service.JoinSequentially(duplicated, duplicated.VideoClips.Reverse().Select(clip => clip.Id).ToArray());
        Assert.All(joined.VideoClips, clip => Assert.Equal(ProjectTime.Zero, clip.SourceStart));
        Assert.Empty(ProjectValidator.Validate(joined));
    }

    [Fact]
    public void ImageAndVideoCannotOverlapAndBoundarySelectsFollowingClip()
    {
        var project = Project();
        var video = new MediaAsset(Guid.NewGuid(), "video.mp4", "media/video/video.mp4", MediaKind.Video,
            ProjectDuration.FromSeconds(10));
        var following = new VideoClip(Guid.NewGuid(), video.Id, ProjectTime.FromSeconds(5), ProjectTime.Zero,
            ProjectDuration.FromSeconds(2), true);
        project = project with { MediaAssets = [.. project.MediaAssets, video], VideoClips = [.. project.VideoClips, following] };
        Assert.Throws<InvalidOperationException>(() => _service.UpdateClip(project, project.VideoClips[0].Id,
            ProjectTime.Zero, ProjectTime.Zero, ProjectDuration.FromSeconds(5.1), true));
        Assert.Equal(following.Id, VideoTimelineService.VisibleClipAt(project, ProjectTime.FromSeconds(5))!.Id);
        Assert.Null(VideoTimelineService.VisibleClipAt(project, ProjectTime.FromSeconds(7)));
        Assert.Empty(ProjectValidator.Validate(project));
    }

    [Fact]
    public void ImageCannotBeTheSongTrack()
    {
        var project = Project();
        Assert.Contains(ProjectValidator.Validate(project with { MasterAudioAssetId = project.MediaAssets[0].Id }),
            error => error.Contains("master audio", StringComparison.OrdinalIgnoreCase));
    }

    private static KaraokeProject Project()
    {
        var image = new MediaAsset(Guid.NewGuid(), "photo.png", "media/images/photo.png", MediaKind.Image, ProjectDuration.Zero);
        return KaraokeProject.Create("Images", DateTimeOffset.UtcNow) with
        {
            MediaAssets = [image],
            VideoClips = [new VideoClip(Guid.NewGuid(), image.Id, ProjectTime.Zero, ProjectTime.Zero,
                VideoTimelineService.DefaultImageDuration, true)],
        };
    }
}
