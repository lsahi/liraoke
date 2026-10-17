using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Rendering;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class LanguageSnapshotRendererTests
{
    [Theory]
    [InlineData(AnnotationType.Pinyin)]
    [InlineData(AnnotationType.Translation)]
    public async Task ExplicitWholeLineAuxiliaryTextMatchesLegacyLayoutWithSnapshot(AnnotationType type)
    {
        var line = Line("你好 love") with
        {
            AnnotationType = type, ReadingText = "ni hao love", AnnotationText = "older reading",
            TranslationText = "Hello, my love", Units = [Unit(new(0, 1), "ni", 0, 1), Unit(new(1, 1), "hao", 1, 2), Unit(new(3, 4), "love", 2, 3)],
        };
        var snapshot = new LyricPresentationSnapshot(line.PrimaryText, [],
            [new(new(0, 2), "must not repeat", null, []), new(new(3, 4), "must not repeat", null, [])]);
        await using var actualRenderer = new WpfSubtitleFrameRenderer(Project(line with { Presentation = snapshot }, LyricLanguage.Auto));
        await using var referenceRenderer = new WpfSubtitleFrameRenderer(Project(line, LyricLanguage.Chinese));
        var actual = await actualRenderer.RenderAsync(ProjectTime.FromSeconds(1.5), 960, 540);
        var expected = await referenceRenderer.RenderAsync(ProjectTime.FromSeconds(1.5), 960, 540);
        Assert.Equal(expected.BgraPixels.ToArray(), actual.BgraPixels.ToArray());
    }

    [Theory]
    [InlineData(AnnotationType.Kana)]
    [InlineData(AnnotationType.Romaji)]
    public async Task NonJapaneseAuxiliaryTextRemainsCompatibleWithoutOverridingMixedJapaneseRuby(AnnotationType type)
    {
        var line = Line("你好 love") with
        {
            AnnotationType = type, ReadingText = "ni hao love", RomanizationText = "ni hao love",
            Units = [Unit(new(0, 1), "ni", 0, 1), Unit(new(1, 1), "hao", 1, 2), Unit(new(3, 4), "love", 2, 3)],
            LanguageSpans = [new(Guid.NewGuid(), new(0, 3), LanguageIds.Chinese, LanguageDetectionSource.Automatic),
                new(Guid.NewGuid(), new(3, 4), LanguageIds.English, LanguageDetectionSource.Automatic)],
            Presentation = new("你好 love", [], [new(new(0, 7), "must not replace the auxiliary line", null, [])]),
        };
        // Even a legacy Japanese document default cannot override explicitly non-Japanese spans.
        await using var actualRenderer = new WpfSubtitleFrameRenderer(Project(line, LyricLanguage.Japanese));
        await using var referenceRenderer = new WpfSubtitleFrameRenderer(Project(line with { Presentation = null }, LyricLanguage.Chinese));
        var actual = await actualRenderer.RenderAsync(ProjectTime.FromSeconds(1.5), 960, 540);
        var expected = await referenceRenderer.RenderAsync(ProjectTime.FromSeconds(1.5), 960, 540);
        Assert.Equal(expected.BgraPixels.ToArray(), actual.BgraPixels.ToArray());

        var japaneseUnit = Unit(new(0, 1), "きみ", 0, 1);
        var englishUnit = Unit(new(2, 4), "love", 1, 2);
        var mixed = Line("君 love") with
        {
            AnnotationType = type, ReadingText = "must not replace ruby", RomanizationText = "must not replace ruby",
            Units = [japaneseUnit, englishUnit],
            LanguageSpans = [new(Guid.NewGuid(), new(0, 2), LanguageIds.Japanese, LanguageDetectionSource.Automatic),
                new(Guid.NewGuid(), new(2, 4), LanguageIds.English, LanguageDetectionSource.Automatic)],
            Presentation = new("君 love", [], [new(new(0, 1), "きみ", "kimi", [japaneseUnit.Id], LanguageIds.Japanese),
                new(new(2, 4), "", null, [englishUnit.Id], LanguageIds.English)]),
        };
        await using var mixedRenderer = new WpfSubtitleFrameRenderer(Project(mixed, LyricLanguage.Chinese));
        await using var mixedReferenceRenderer = new WpfSubtitleFrameRenderer(Project(mixed with { AnnotationType = AnnotationType.None }, LyricLanguage.Chinese));
        var mixedActual = await mixedRenderer.RenderAsync(ProjectTime.FromSeconds(0.5), 960, 540);
        var mixedExpected = await mixedReferenceRenderer.RenderAsync(ProjectTime.FromSeconds(0.5), 960, 540);
        Assert.Equal(mixedExpected.BgraPixels.ToArray(), mixedActual.BgraPixels.ToArray());
    }

    [Fact]
    public async Task MixedSnapshotRenderingDoesNotDependOnDocumentLanguageOrInstalledPlugins()
    {
        var line = Line("君 love 你");
        var japanese = Unit(new(0, 1), "きみ", 0, 1);
        var english = Unit(new(2, 4), "love", 1, 2);
        var chinese = Unit(new(7, 1), "你", 2, 3);
        line = line with
        {
            Units = [japanese, english, chinese],
            Presentation = new(line.PrimaryText, [],
                [new(new(0, 1), "きみ", "kimi", [japanese.Id], LanguageIds.Japanese),
                 new(new(2, 4), "", null, [english.Id], LanguageIds.English),
                 new(new(7, 1), "", null, [chinese.Id], LanguageIds.Chinese)]),
        };
        byte[]? reference = null;
        foreach (var language in new[] { LyricLanguage.Auto, LyricLanguage.Chinese, LyricLanguage.English, LyricLanguage.Japanese })
        {
            await using var renderer = new WpfSubtitleFrameRenderer(Project(line, language));
            var frame = await renderer.RenderAsync(ProjectTime.FromSeconds(1.5), 960, 540);
            byte[] pixels = frame.BgraPixels.ToArray();
            Assert.Contains(pixels, value => value != 0);
            if (reference is null) { reference = pixels; }
            else { Assert.Equal(reference, pixels); }
        }
    }

    [Fact]
    public async Task SnapshotKeepsOrthographicRubyIndependentOfSingingUnitsAndRomanizationToggle()
    {
        var first = Unit(new(0, 2), "は", 0, 1);
        var second = Unit(new(0, 2), "しっ", 1, 2);
        var line = Line("走っ") with
        {
            Units = [first, second],
            Presentation = new("走っ", [],
                [new(new(0, 1), "はし", null, [first.Id, second.Id]), new(new(1, 1), "", null, [second.Id])],
                RomanizedSubtitleRuns: [new(new(0, 2), "", "ha shit", [first.Id, second.Id])]),
        };
        var referenceLine = line with
        {
            Presentation = null,
            Units = [Unit(new(0, 1), "はし", 0, 1), Unit(new(1, 1), "っ", 1, 2)],
        };
        await using var actualRenderer = new WpfSubtitleFrameRenderer(Project(line, LyricLanguage.Auto));
        await using var referenceRenderer = new WpfSubtitleFrameRenderer(Project(referenceLine, LyricLanguage.Japanese));
        var actual = await actualRenderer.RenderAsync(ProjectTime.Zero, 960, 540);
        var expected = await referenceRenderer.RenderAsync(ProjectTime.Zero, 960, 540);
        Assert.Equal(expected.BgraPixels.ToArray(), actual.BgraPixels.ToArray());

        var project = Project(line, LyricLanguage.Auto);
        await using var romajiRenderer = new WpfSubtitleFrameRenderer(project with { SubtitleStyle = project.SubtitleStyle with { ShowRomanization = true } });
        var romaji = await romajiRenderer.RenderAsync(ProjectTime.Zero, 960, 540);
        Assert.False(actual.BgraPixels.Span.SequenceEqual(romaji.BgraPixels.Span));
    }

    private static LyricLine Line(string text) => new(Guid.NewGuid(), text, AnnotationType.None, null, null,
        new(ProjectTime.Zero, ProjectTime.FromSeconds(4)), [], ReviewState.Unreviewed, null);
    private static LyricUnit Unit(TextRange range, string reading, double start, double end) => new(Guid.NewGuid(),
        [range], reading, null, new(ProjectTime.FromSeconds(start), ProjectTime.FromSeconds(end)), null, true);
    private static KaraokeProject Project(LyricLine line, LyricLanguage language) =>
        KaraokeProject.Create("Language snapshot", DateTimeOffset.UtcNow) with { Lyrics = new(language, [line]) };
}
