using System.Text.Json;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Validation;

namespace KaraokeFilmMaker.Domain.Tests;

public sealed class SubtitleStyleCompatibilityTests
{
    [Fact]
    public void NewProjectsUseBrandColorsAndLegacyStylesKeepOneOutline()
    {
        var style = KaraokeProject.Create("New", DateTimeOffset.UtcNow).SubtitleStyle;
        Assert.Equal("#FFFFFFFF", style.InactiveColor);
        Assert.Equal("#FFFFD54F", style.ActiveColor);
        Assert.Equal("#FF24465F", style.OutlineColor);
        Assert.Equal("#FFFFFFFF", style.ActiveOutlineColor);
        string legacy = """{"FontFamily":"Yu Gothic UI","PrimaryFontSize":64,"AnnotationFontSize":28,"InactiveColor":"#FFFFFF","ActiveColor":"#FFD54F","OutlineColor":"#000000","OutlineWidth":3,"ShadowOffset":2}""";
        Assert.Null(JsonSerializer.Deserialize<SubtitleStyle>(legacy)!.ActiveOutlineColor);
        Assert.Equal(style, JsonSerializer.Deserialize<SubtitleStyle>(JsonSerializer.Serialize(style)));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("#FFFFFF", true)]
    [InlineData("#80FFFFFF", true)]
    [InlineData("white", false)]
    [InlineData("#ZZFFFF", false)]
    [InlineData("", false)]
    public void OptionalSungOutlineRequiresAValidHexColor(string? value, bool valid)
    {
        var project = KaraokeProject.Create("Colors", DateTimeOffset.UtcNow);
        project = project with { SubtitleStyle = project.SubtitleStyle with { ActiveOutlineColor = value } };
        Assert.Equal(valid, ProjectValidator.Validate(project).Count == 0);
    }
}
