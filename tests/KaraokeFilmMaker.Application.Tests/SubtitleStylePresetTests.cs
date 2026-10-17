using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Validation;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class SubtitleStylePresetTests
{
    [Fact]
    public void AllPresetsAreValidAndPreserveFontLayoutAndReadingPreference()
    {
        var project = KaraokeProject.Create("Presets", DateTimeOffset.UtcNow);
        var customized = project.SubtitleStyle with
        { FontFamily = "User's font", PrimaryFontSize = 77, AnnotationFontSize = 31, MarginRatio = 0.1, LineGap = 12, ShowRomanization = true };
        Assert.Equal(SubtitleStylePresets.All.Count, SubtitleStylePresets.All.Select(preset => preset.Id).Distinct().Count());
        foreach (var preset in SubtitleStylePresets.All)
        {
            var applied = preset.ApplyTo(customized);
            Assert.Empty(ProjectValidator.Validate(project with { SubtitleStyle = applied }));
            Assert.Equal(customized.FontFamily, applied.FontFamily);
            Assert.Equal(customized.PrimaryFontSize, applied.PrimaryFontSize);
            Assert.Equal(customized.AnnotationFontSize, applied.AnnotationFontSize);
            Assert.Equal(customized.MarginRatio, applied.MarginRatio);
            Assert.Equal(customized.LineGap, applied.LineGap);
            Assert.Equal(customized.ShowRomanization, applied.ShowRomanization);
        }
        Assert.Equal(project.SubtitleStyle, SubtitleStylePresets.All[0].ApplyTo(project.SubtitleStyle));
    }
}
