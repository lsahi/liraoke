using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Validation;

namespace KaraokeFilmMaker.Domain.Tests;

public sealed class AudioVisualizationStyleTests
{
    [Fact]
    public void DefaultIsDisabledClassicAndAllFiveStylesAreValid()
    {
        var project = KaraokeProject.Create("频谱", DateTimeOffset.UtcNow);
        Assert.False(project.AudioVisualizationEnabled);
        Assert.Equal(AudioVisualizationStyle.Classic, project.AudioVisualizationStyle);
        Assert.Equal(5, Enum.GetValues<AudioVisualizationStyle>().Length);
        foreach (var style in Enum.GetValues<AudioVisualizationStyle>())
        { Assert.Empty(ProjectValidator.Validate(project with { AudioVisualizationStyle = style })); }
        Assert.Contains(ProjectValidator.Validate(project with { AudioVisualizationStyle = (AudioVisualizationStyle)99 }),
            error => error.Contains("visualization"));
    }
}
