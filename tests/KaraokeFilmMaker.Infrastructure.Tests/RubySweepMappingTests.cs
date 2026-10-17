using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Rendering;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class RubySweepMappingTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task DeWaitsForItsPrimaryGlyphInLegacyAndSnapshotRendering(bool snapshot, bool remapped, bool romanized)
    {
        var line = new LyricLine(Guid.NewGuid(), "思い出", AnnotationType.Kana, "おもいで", null,
            new(ProjectTime.Zero, ProjectTime.FromSeconds(5)),
            new[] { "お", "も", "い", "で" }.Select((reading, i) => new LyricUnit(Guid.NewGuid(),
                remapped ? [new(i < 2 ? 0 : i - 1, 1)] : [new(0, 3)], reading, new[] { "o", "mo", "i", "de" }[i],
                new(ProjectTime.FromSeconds(i), ProjectTime.FromSeconds(i + 1)), null, true, LanguageIds.Japanese)).ToArray(),
            ReviewState.Confirmed, ReadingSource.Confirmed);
        if (snapshot) { line = line with { Presentation = LanguagePresentation.BuildJapanese(line) }; }
        var project = KaraokeProject.Create("思い出", DateTimeOffset.UtcNow) with { Lyrics = new(snapshot ? LyricLanguage.Auto : LyricLanguage.Japanese, [line]) };
        project = project with { SubtitleStyle = project.SubtitleStyle with
        { PrimaryFontSize = 64, AnnotationFontSize = 24, LineGap = 10, MarginRatio = 0, ShadowOffset = 0, OutlineWidth = 0, ShowRomanization = romanized } };
        await using var renderer = new WpfSubtitleFrameRenderer(project);
        var before = await renderer.RenderAsync(ProjectTime.Zero, 1920, 1080);
        var middle = await renderer.RenderAsync(ProjectTime.FromSeconds(2), 1920, 1080);
        var partial = await renderer.RenderAsync(ProjectTime.FromSeconds(3.5), 1920, 1080);
        var finished = await renderer.RenderAsync(ProjectTime.FromSeconds(4), 1920, 1080);
        // With these sizes each primary ideograph occupies 64px, and ruby remains above y=798.
        // The third glyph starts at x=140 (12px margin + 2 * 64px).
        Assert.True(Count(finished, 140, 204, 756, 798) > 10, "The last ruby must exist and become yellow when 出 is sung.");
        Assert.Equal(0, Count(middle, 140, 204, 756, 798));
        Assert.Equal(Crop(before), Crop(middle));
        int partialRuby = Count(partial, 140, 204, 756, 798);
        Assert.InRange(partialRuby, 1, Count(finished, 140, 204, 756, 798) - 1);
        Assert.True(Count(partial, 140, 204, 798, 900) > 10, "The primary 出 is swept at the same time.");
        Assert.Equal(0, Count(middle, 140, 204, 798, 900));
        Assert.Equal(middle.BgraPixels.ToArray(), (await renderer.RenderAsync(ProjectTime.FromSeconds(2), 1920, 1080)).BgraPixels.ToArray());
        Save(middle, $"ruby-waits-{snapshot}-{remapped}-{romanized}.png");
        Save(partial, $"ruby-follows-{snapshot}-{remapped}-{romanized}.png");
    }

    private static int Count(RenderedFrame frame, int left, int right, int top, int bottom)
    {
        var pixels = frame.BgraPixels.Span; int count = 0;
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
        {
            int offset = y * frame.Stride + x * 4;
            if (pixels[offset] == 79 && pixels[offset + 1] == 213 && pixels[offset + 2] == 255 && pixels[offset + 3] == 255) { count++; }
        }
        return count;
    }
    private static byte[] Crop(RenderedFrame frame) => Enumerable.Range(756, 42)
        .SelectMany(y => frame.BgraPixels.Slice(y * frame.Stride + 140 * 4, 64 * 4).ToArray()).ToArray();
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
