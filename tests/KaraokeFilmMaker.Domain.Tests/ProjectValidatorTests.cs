using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;
using KaraokeFilmMaker.Domain.Validation;

namespace KaraokeFilmMaker.Domain.Tests;

public sealed class ProjectValidatorTests
{
    [Fact]
    public void AcceptsVideoGapsButRejectsOverlaps()
    {
        Guid mediaId = Guid.NewGuid();
        var firstClip = Clip(mediaId, startSeconds: 0, durationSeconds: 5);
        var secondClip = Clip(mediaId, startSeconds: 6, durationSeconds: 5);
        KaraokeProject project = Project(mediaId, [firstClip, secondClip]);

        Assert.Empty(ProjectValidator.Validate(project));

        var overlapping = secondClip with { TimelineStart = ProjectTime.FromSeconds(4) };
        project = project with { VideoClips = [firstClip, overlapping] };

        Assert.Contains(
            ProjectValidator.Validate(project),
            error => error.Contains("重叠", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("transparent")]
    [InlineData("#XYZXYZ")]
    public void RejectsInvalidSubtitleColorsBeforeOpeningOrRendering(string color)
    {
        KaraokeProject project = KaraokeProject.Create("Invalid style", DateTimeOffset.UtcNow);
        project = project with { SubtitleStyle = project.SubtitleStyle with { ActiveColor = color } };
        Assert.Contains(ProjectValidator.Validate(project), error => error.Contains("Subtitle style", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(60)]
    public void AcceptsSupportedExportFrameRates(int framesPerSecond)
    {
        Guid mediaId = Guid.NewGuid();
        KaraokeProject project = Project(mediaId, []) with
        {
            ExportPreset = new ExportPreset(1920, 1080, framesPerSecond, 12_000, 320),
        };

        Assert.Empty(ProjectValidator.Validate(project));
    }

    private static VideoClip Clip(Guid mediaId, double startSeconds, double durationSeconds) =>
        new(
            Guid.NewGuid(),
            mediaId,
            ProjectTime.FromSeconds(startSeconds),
            ProjectTime.Zero,
            ProjectDuration.FromSeconds(durationSeconds),
            IsMuted: true);

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1440, 1080)]
    [InlineData(1080, 1080)]
    [InlineData(1080, 1920)]
    [InlineData(1280, 720)]
    [InlineData(960, 720)]
    [InlineData(720, 720)]
    [InlineData(720, 1280)]
    [InlineData(320, 240)]
    [InlineData(1, 1)]
    public void AcceptsLandscapeSquarePortraitAndLegacyLowResolution(int width, int height)
    {
        var project = KaraokeProject.Create("Output", DateTimeOffset.UtcNow);
        project = project with { ExportPreset = project.ExportPreset with { Width = width, Height = height } };
        Assert.Empty(ProjectValidator.Validate(project));
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1080, 0)]
    [InlineData(-1, 1080)]
    [InlineData(1921, 1080)]
    [InlineData(1080, 1921)]
    [InlineData(1920, 1081)]
    [InlineData(1081, 1920)]
    [InlineData(1920, 1920)]
    public void RejectsNonPositiveOrOversizeOutputDimensions(int width, int height)
    {
        var project = KaraokeProject.Create("Invalid output", DateTimeOffset.UtcNow);
        project = project with { ExportPreset = project.ExportPreset with { Width = width, Height = height } };
        Assert.Contains(ProjectValidator.Validate(project), error => error.Contains("resolution", StringComparison.Ordinal));
    }

    private static KaraokeProject Project(Guid mediaId, IReadOnlyList<VideoClip> clips)
    {
        var media = new MediaAsset(
            mediaId,
            "background.mp4",
            "media/video/background.mp4",
            MediaKind.Video,
            ProjectDuration.FromSeconds(30));

        return new KaraokeProject(
            KaraokeProject.CurrentSchemaVersion,
            Guid.NewGuid(),
            "Test project",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            MasterAudioAssetId: null,
            MediaAssets: [media],
            Lyrics: new LyricDocument(LyricLanguage.Japanese, []),
            VideoClips: clips,
            SubtitleStyle: new SubtitleStyle(
                "Yu Gothic UI",
                64,
                28,
                "#FFFFFFFF",
                "#FFFFD700",
                "#FF000000",
                3,
                2),
            ExportPreset: new ExportPreset(1920, 1080, 30, 12_000, 320));
    }
}
