using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Rendering;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class SubtitleOutlineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PunctuationAlsoReceivesTheSungOutlineWhenTheLineCompletes(bool snapshot)
    {
        var line = new LyricLine(Guid.NewGuid(), "\"La, la!\"", AnnotationType.None, null, null,
            new(ProjectTime.Zero, ProjectTime.FromSeconds(8)),
            [new(Guid.NewGuid(), [new(1, 2)], "La", null, new(ProjectTime.FromSeconds(1), ProjectTime.FromSeconds(2)), null, true),
             new(Guid.NewGuid(), [new(5, 2)], "la", null, new(ProjectTime.FromSeconds(3), ProjectTime.FromSeconds(4)), null, true)],
            ReviewState.Confirmed, null);
        if (snapshot) { line = line with { Presentation = LanguagePresentation.BuildPlain(line) }; }
        var project = KaraokeProject.Create("Punctuation outline", DateTimeOffset.UtcNow) with { Lyrics = new(LyricLanguage.English, [line]) };
        project = project with { SubtitleStyle = project.SubtitleStyle with { ShadowOffset = 0 } };
        await using var renderer = new WpfSubtitleFrameRenderer(project);
        var completed = await renderer.RenderAsync(ProjectTime.FromSeconds(4), 1920, 1080);
        Assert.Equal(0, Count(completed, 0, 1920, 36, 70, 95));
        Assert.True(Count(completed, 0, 1920, 255, 255, 255) > 10);
        Assert.True(Count(completed, 0, 1920, 255, 213, 79) > 20);
    }

    [Theory]
    [InlineData(LyricLanguage.English, false, false)]
    [InlineData(LyricLanguage.English, true, false)]
    [InlineData(LyricLanguage.Chinese, true, false)]
    [InlineData(LyricLanguage.Japanese, false, false)]
    [InlineData(LyricLanguage.Japanese, true, false)]
    [InlineData(LyricLanguage.Japanese, true, true)]
    public async Task SungWhiteOutlineAndYellowFillDoNotChangeTheUnsungNextWord(LyricLanguage language, bool snapshot, bool romanized)
    {
        bool japanese = language == LyricLanguage.Japanese;
        string first = japanese ? "駅" : language == LyricLanguage.Chinese ? "你" : "La";
        string second = japanese ? "歌" : language == LyricLanguage.Chinese ? "好" : "Bye";
        string text = first + new string(' ', 40) + second;
        var units = new[]
        {
            new LyricUnit(Guid.NewGuid(), [new(0, first.Length)], japanese ? "えき" : first, japanese ? "eki" : null,
                new(ProjectTime.FromSeconds(1), ProjectTime.FromSeconds(2)), null, true),
            new LyricUnit(Guid.NewGuid(), [new(first.Length + 40, second.Length)], japanese ? "うた" : second, japanese ? "uta" : null,
                new(ProjectTime.FromSeconds(3), ProjectTime.FromSeconds(4)), null, true),
        };
        var line = new LyricLine(Guid.NewGuid(), text, AnnotationType.None, null, null,
            new(ProjectTime.Zero, ProjectTime.FromSeconds(8)), units, ReviewState.Confirmed, null);
        if (snapshot) { line = line with { Presentation = japanese ? LanguagePresentation.BuildJapanese(line) : LanguagePresentation.BuildPlain(line) }; }
        var project = KaraokeProject.Create("Outline", DateTimeOffset.UtcNow) with { Lyrics = new(language, [line]) };
        // No shadow makes the stroke-color assertion independent from the deliberately retained shadow.
        project = project with { SubtitleStyle = project.SubtitleStyle with { ShowRomanization = romanized, ShadowOffset = 0 } };
        await using var renderer = new WpfSubtitleFrameRenderer(project);
        RenderedFrame before = await renderer.RenderAsync(ProjectTime.Zero, 1920, 1080);
        RenderedFrame partial = await renderer.RenderAsync(ProjectTime.FromSeconds(1.5), 1920, 1080);
        RenderedFrame firstComplete = await renderer.RenderAsync(ProjectTime.FromSeconds(2), 1920, 1080);
        Assert.True(Count(before, 0, 300, 36, 70, 95) > 10, "The unsung glyph has a deep-blue outline.");
        Assert.True(Count(firstComplete, 0, 300, 255, 213, 79) > 20, "The sung glyph has yellow fill.");
        Assert.True(Count(firstComplete, 0, 300, 255, 255, 255) > 10, "The sung glyph has a white outline.");
        Assert.Equal(0, Count(firstComplete, 0, 300, 36, 70, 95));
        Assert.True(Count(partial, 0, 300, 36, 70, 95) > 0);
        Assert.True(Count(partial, 0, 300, 255, 213, 79) > 0);
        Assert.Equal(Crop(before, 600), Crop(firstComplete, 600));
        Assert.True(Count(firstComplete, 600, 1920, 36, 70, 95) > 0, "The next unsung glyph retains its original outline.");
        if (japanese)
        {
            Assert.True(Count(firstComplete, 0, 300, 255, 213, 79, 756, 800) > 0, "Ruby/romaji also follows the sung fill.");
            Assert.True(Count(firstComplete, 0, 300, 255, 255, 255, 756, 800) > 0, "Ruby/romaji also follows the sung outline.");
        }
        Assert.Equal(partial.BgraPixels.ToArray(), (await renderer.RenderAsync(ProjectTime.FromSeconds(1.5), 1920, 1080)).BgraPixels.ToArray());
    }

    [Fact]
    public async Task MissingFontUsesTheSameExplicitLocalFallbackAsPreviewWithoutChangingProject()
    {
        var line = new LyricLine(Guid.NewGuid(), "字体 / Font", AnnotationType.None, null, null,
            new(ProjectTime.Zero, ProjectTime.FromSeconds(5)), [], ReviewState.Unreviewed, null);
        var project = KaraokeProject.Create("Font", DateTimeOffset.UtcNow) with { Lyrics = new(LyricLanguage.Chinese, [line]) };
        project = project with { SubtitleStyle = project.SubtitleStyle with { FontFamily = "Definitely not installed 7349" } };
        Assert.False(SystemFontCatalog.IsInstalled(project.SubtitleStyle.FontFamily));
        Assert.NotEmpty(SystemFontCatalog.Names);
        Assert.All(SystemFontCatalog.Search("Gothic"), name => Assert.True(SystemFontCatalog.IsInstalled(name)));
        await using var missing = new WpfSubtitleFrameRenderer(project);
        await using var fallback = new WpfSubtitleFrameRenderer(project with
        { SubtitleStyle = project.SubtitleStyle with { FontFamily = SystemFontCatalog.Resolve(project.SubtitleStyle.FontFamily) } });
        Assert.Equal((await fallback.RenderAsync(ProjectTime.Zero, 960, 540)).BgraPixels.ToArray(),
            (await missing.RenderAsync(ProjectTime.Zero, 960, 540)).BgraPixels.ToArray());
        Assert.Equal("Definitely not installed 7349", project.SubtitleStyle.FontFamily);
    }

    private static int Count(RenderedFrame frame, int x0, int x1, byte r, byte g, byte b, int y0 = 0, int? y1 = null)
    {
        var pixels = frame.BgraPixels.Span; int count = 0;
        for (int y = y0; y < (y1 ?? frame.Height); y++)
        for (int x = x0; x < x1; x++)
        {
            int i = y * frame.Stride + x * 4;
            if (pixels[i + 3] > 250 && pixels[i] == b && pixels[i + 1] == g && pixels[i + 2] == r) { count++; }
        }
        return count;
    }
    private static byte[] Crop(RenderedFrame frame, int x0)
    {
        var result = new byte[(frame.Width - x0) * frame.Height * 4];
        for (int y = 0; y < frame.Height; y++)
        { frame.BgraPixels.Span.Slice(y * frame.Stride + x0 * 4, (frame.Width - x0) * 4).CopyTo(result.AsSpan(y * (frame.Width - x0) * 4)); }
        return result;
    }
}
