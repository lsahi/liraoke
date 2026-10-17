using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Rendering;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class JapaneseFontConsistencyTests
{
    [Theory]
    [InlineData(1920, 1080, 64, false)]
    [InlineData(1920, 1080, 192, false)]
    [InlineData(1280, 720, 192, false)]
    [InlineData(1440, 1080, 192, false)]
    [InlineData(1080, 1080, 192, false)]
    [InlineData(1080, 1920, 192, false)]
    [InlineData(1920, 1080, 192, true)]
    [InlineData(1080, 1920, 192, true)]
    public async Task PrimaryGlyphsKeepExactlyTheSameSizeAndBaselineWithOrWithoutRuby(int width, int height, double fontSize, bool bottomSlot)
    {
        var line = Line();
        var unit = line.Units[0];
        LyricLine WithRuby(string annotation) => line with
        {
            Presentation = new(line.PrimaryText, [new(unit.Id, "かな")],
                [new(new(0, 2), annotation, null, [unit.Id], LanguageIds.Japanese)]),
        };
        var empty = line with { Id = Guid.NewGuid(), PrimaryText = "前", Timing = new(ProjectTime.Zero, ProjectTime.FromSeconds(0.5)), Units = [] };
        KaraokeProject Project(LyricLine current) => KaraokeProject.Create("日语字幕一致性", DateTimeOffset.UtcNow) with
        {
            // Japanese persisted runs must work in an auto/mixed-language document too.
            Lyrics = new(LyricLanguage.Auto, bottomSlot ? [empty, current] : [current]),
            SubtitleStyle = Style(fontSize),
        };
        await using var annotatedRenderer = new WpfSubtitleFrameRenderer(Project(WithRuby("かな")));
        await using var plainRenderer = new WpfSubtitleFrameRenderer(Project(WithRuby("")));
        var annotated = await annotatedRenderer.RenderAsync(ProjectTime.FromSeconds(1), width, height);
        var plain = await plainRenderer.RenderAsync(ProjectTime.FromSeconds(1), width, height);
        AssertSamePrimaryPixels(plain, annotated);
        Save(plain, $"japanese-no-ruby-{width}-{height}-{fontSize}-{bottomSlot}.png");
        Save(annotated, $"japanese-with-ruby-{width}-{height}-{fontSize}-{bottomSlot}.png");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyKanaOnlyLinesKeepPrimarySizeWhenRomanizationIsToggled(bool snapshot)
    {
        var line = Line();
        if (snapshot)
        {
            line = line with { Presentation = new(line.PrimaryText, [new(line.Units[0].Id, "かな")],
                [new(new(0, 2), "", "kana", [line.Units[0].Id], LanguageIds.Japanese)]) };
        }
        var project = KaraokeProject.Create("かな", DateTimeOffset.UtcNow) with { Lyrics = new(LyricLanguage.Japanese, [line]), SubtitleStyle = Style(192) };
        await using var plainRenderer = new WpfSubtitleFrameRenderer(project);
        await using var annotatedRenderer = new WpfSubtitleFrameRenderer(project with { SubtitleStyle = project.SubtitleStyle with { ShowRomanization = true } });
        AssertSamePrimaryPixels(await plainRenderer.RenderAsync(ProjectTime.FromSeconds(1), 1920, 1080),
            await annotatedRenderer.RenderAsync(ProjectTime.FromSeconds(1), 1920, 1080));
    }

    private static LyricLine Line() => new(Guid.NewGuid(), "かな", AnnotationType.None, null, null,
        new(ProjectTime.FromSeconds(1), ProjectTime.FromSeconds(4)),
        [new(Guid.NewGuid(), [new(0, 2)], "かな", "kana", new(ProjectTime.FromSeconds(1), ProjectTime.FromSeconds(4)), null, true)],
        ReviewState.Confirmed, ReadingSource.Confirmed);

    private static SubtitleStyle Style(double size) => KaraokeProject.Create("style", DateTimeOffset.UtcNow).SubtitleStyle with
    { PrimaryFontSize = size, AnnotationFontSize = size * 0.4375, ShadowOffset = 0, OutlineWidth = 0, MarginRatio = 0 };

    private static void AssertSamePrimaryPixels(RenderedFrame plain, RenderedFrame annotated)
    {
        var pixels = plain.BgraPixels.Span;
        int firstRow = plain.Height, lastRow = -1;
        for (int y = 0; y < plain.Height; y++)
        for (int x = 0; x < plain.Width; x++)
        {
            if (pixels[y * plain.Stride + x * 4 + 3] == 0) { continue; }
            firstRow = Math.Min(firstRow, y); lastRow = y;
        }
        Assert.True(lastRow >= firstRow);
        // Every primary glyph pixel must match, not just a nominal font-size setting.
        Assert.Equal(plain.BgraPixels.Slice(firstRow * plain.Stride, (lastRow - firstRow + 1) * plain.Stride).ToArray(),
            annotated.BgraPixels.Slice(firstRow * annotated.Stride, (lastRow - firstRow + 1) * annotated.Stride).ToArray());
        Assert.True(annotated.BgraPixels.Span[..(firstRow * annotated.Stride)].ContainsAnyExcept((byte)0));
    }

    private static void Save(RenderedFrame frame, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (directory is null) { return; }
        Directory.CreateDirectory(directory);
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.BgraPixels.ToArray(), frame.Stride);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }
}
