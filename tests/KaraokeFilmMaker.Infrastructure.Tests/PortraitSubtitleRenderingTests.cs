using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Rendering;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class PortraitSubtitleRenderingTests
{
    [Theory]
    [InlineData(false, false, 1080)]
    [InlineData(false, true, 1080)]
    [InlineData(true, false, 1080)]
    [InlineData(true, true, 1080)]
    [InlineData(false, false, 720)]
    [InlineData(true, true, 720)]
    public async Task PortraitUsesSameGlyphAndRubyScaleAsLandscapeWithEqualShortEdge(bool japanese, bool snapshot, int shortEdge)
    {
        var project = CreateProject(japanese, snapshot);
        // Isolate font scaling from viewport-relative margins: the default 4% produces
        // fractional origins (76.8 vs 43.2 at 1080p), which change WPF's edge rasterization.
        // Zero ratio selects the same short-edge-scaled minimum margin in both orientations.
        project = project with { SubtitleStyle = project.SubtitleStyle with { MarginRatio = 0 } };
        await using var renderer = new WpfSubtitleFrameRenderer(project);
        int longEdge = shortEdge * 16 / 9;
        var landscape = await renderer.RenderAsync(ProjectTime.FromSeconds(1), longEdge, shortEdge);
        var portrait = await renderer.RenderAsync(ProjectTime.FromSeconds(1), shortEdge, longEdge);
        var landscapeBounds = VisibleBounds(landscape, 0, shortEdge);
        var portraitBounds = VisibleBounds(portrait, 0, longEdge);

        // Placement differs, but changing orientation must not inflate the font, ruby or outlines.
        Assert.InRange(Math.Abs(portraitBounds.Width - landscapeBounds.Width), 0, 2);
        Assert.InRange(Math.Abs(portraitBounds.Height - landscapeBounds.Height), 0, 2);
        Assert.InRange(portraitBounds.Top, (int)(longEdge * 0.7), longEdge - 1);
        Assert.InRange(portraitBounds.Left, Math.Max(0, (int)(12 * shortEdge / 1080.0) - 3), shortEdge / 2);
        Assert.True(CountGold(portrait) > 10);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongPortraitLyricsFitBothBottomAnchoredSlots(bool japanese)
    {
        string text = japanese ? string.Concat(Enumerable.Repeat("未来へ進む", 10))
            : string.Join(' ', Enumerable.Repeat("Keep singing with us", 8));
        var project = CreateProject(japanese, true, text);
        var first = project.Lyrics.Lines[0];
        var next = first with { Id = Guid.NewGuid(), Timing = new(ProjectTime.FromSeconds(5), ProjectTime.FromSeconds(10)) };
        project = project with { Lyrics = project.Lyrics with { Lines = [first, next] } };
        await using var renderer = new WpfSubtitleFrameRenderer(project);
        var frame = await renderer.RenderAsync(ProjectTime.FromSeconds(1), 720, 1280);

        var rows = Enumerable.Range(0, frame.Height).Where(y => CountVisible(frame, y, y + 1) > 0).ToArray();
        int split = rows.Zip(rows.Skip(1), (a, b) => (Gap: b - a, Split: (a + b) / 2))
            .MaxBy(pair => pair.Gap).Split;
        var top = VisibleBounds(frame, 0, split);
        var bottom = VisibleBounds(frame, split, frame.Height);
        Assert.InRange(top.Left, 10, 40);
        Assert.InRange(top.Right, 640, 710);
        Assert.InRange(bottom.Left, 10, 80);
        Assert.InRange(bottom.Right, 680, 710);
        Assert.True(top.Bottom < bottom.Top);
        Assert.Equal(0, CountVisible(frame, 0, 896));
    }

    [Theory]
    [InlineData(1080, 1921)]
    [InlineData(1081, 1920)]
    public async Task OffscreenRendererStillRejectsDimensionsAboveTheLongAndShortEdgeLimits(int width, int height)
    {
        await using var renderer = new WpfSubtitleFrameRenderer(CreateProject(false, false));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await renderer.RenderAsync(ProjectTime.Zero, width, height));
    }

    private static KaraokeProject CreateProject(bool japanese, bool snapshot, string? source = null)
    {
        string text = source ?? (japanese ? "歌" : "Sing");
        var unit = new LyricUnit(Guid.NewGuid(), [new TextRange(0, text.Length)], japanese ? "うた" : text,
            japanese ? "uta" : null, new(ProjectTime.Zero, ProjectTime.FromSeconds(4)), null, true);
        var line = new LyricLine(Guid.NewGuid(), text, AnnotationType.None, null, null,
            new(ProjectTime.Zero, ProjectTime.FromSeconds(4)), [unit], ReviewState.Confirmed, null);
        if (snapshot)
        {
            line = line with { Presentation = japanese ? LanguagePresentation.BuildJapanese(line) : LanguagePresentation.BuildPlain(line) };
        }
        return KaraokeProject.Create("Portrait subtitles", DateTimeOffset.UtcNow) with
        {
            Lyrics = new LyricDocument(japanese ? LyricLanguage.Japanese : LyricLanguage.English, [line]),
        };
    }

    private static (int Left, int Top, int Right, int Bottom, int Width, int Height) VisibleBounds(RenderedFrame frame, int top, int bottom)
    {
        int minX = frame.Width, minY = frame.Height, maxX = -1, maxY = -1;
        var pixels = frame.BgraPixels.Span;
        for (int y = top; y < bottom; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                if (pixels[y * frame.Stride + x * 4 + 3] < 32) { continue; }
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
        }
        Assert.True(maxX >= minX && maxY >= minY, "Expected visible subtitle pixels.");
        return (minX, minY, maxX, maxY, maxX - minX + 1, maxY - minY + 1);
    }

    private static int CountVisible(RenderedFrame frame, int top, int bottom)
    {
        int count = 0;
        var pixels = frame.BgraPixels.Span;
        for (int i = top * frame.Stride + 3; i < bottom * frame.Stride; i += 4)
        {
            if (pixels[i] > 0) { count++; }
        }
        return count;
    }

    private static int CountGold(RenderedFrame frame)
    {
        int count = 0;
        var pixels = frame.BgraPixels.Span;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] > 200 && pixels[i + 2] > 230 && pixels[i + 1] is > 180 and < 230 && pixels[i] < 110)
            { count++; }
        }
        return count;
    }
}
