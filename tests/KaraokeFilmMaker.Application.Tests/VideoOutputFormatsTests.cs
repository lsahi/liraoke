using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Validation;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class VideoOutputFormatsTests
{
    [Theory]
    [InlineData("16:9", 1080, 1920, 1080)]
    [InlineData("4:3", 1080, 1440, 1080)]
    [InlineData("1:1", 1080, 1080, 1080)]
    [InlineData("9:16", 1080, 1080, 1920)]
    [InlineData("16:9", 720, 1280, 720)]
    [InlineData("4:3", 720, 960, 720)]
    [InlineData("1:1", 720, 720, 720)]
    [InlineData("9:16", 720, 720, 1280)]
    public void MapsAndIdentifiesEverySupportedRatioAtBothResolutions(string ratio, int standard, int width, int height)
    {
        Assert.Equal((width, height), VideoOutputFormats.GetDimensions(ratio, standard));
        Assert.True(VideoOutputFormats.TryIdentify(width, height, out string detectedRatio, out int detectedStandard));
        Assert.Equal(ratio, detectedRatio);
        Assert.Equal(standard, detectedStandard);

        var project = KaraokeProject.Create("Output sizes", DateTimeOffset.UtcNow);
        var customized = project.ExportPreset with { FramesPerSecond = 60, VideoBitrateKbps = 8765, AudioBitrateKbps = 192 };
        var applied = VideoOutputFormats.ApplyTo(customized, ratio, standard);
        Assert.Equal(customized with { Width = width, Height = height }, applied);
        Assert.Empty(ProjectValidator.Validate(project with { ExportPreset = applied }));
    }

    [Theory]
    [InlineData("3:4", 1080)]
    [InlineData("16:9", 2160)]
    [InlineData("9:16", 0)]
    [InlineData("", 720)]
    public void RejectsUnsupportedChoices(string ratio, int standard) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoOutputFormats.GetDimensions(ratio, standard));

    [Theory]
    [InlineData(320, 240)]
    [InlineData(1080, 1440)]
    [InlineData(1921, 1080)]
    [InlineData(0, 0)]
    public void DoesNotMisidentifyCustomOrInvalidDimensions(int width, int height)
    {
        Assert.False(VideoOutputFormats.TryIdentify(width, height, out string ratio, out int standard));
        Assert.Equal(string.Empty, ratio);
        Assert.Equal(0, standard);
    }
}
