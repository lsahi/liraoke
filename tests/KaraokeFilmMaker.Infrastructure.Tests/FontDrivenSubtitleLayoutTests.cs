using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Rendering;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Export;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Infrastructure.Workspaces;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class FontDrivenSubtitleLayoutTests
{
    [Fact]
    public async Task RealMp4KeepsBothLargeRowsAndMatchesSharedRenderBounds()
    {
        string root = Path.Combine(Path.GetTempPath(), "Liraoke.FontExport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tools = MediaToolchain.Find();
            string song = Path.Combine(root, "song.wav");
            await MediaProcess.RunAsync(tools.FfmpegPath,
                ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", song]);
            var service = new WorkspaceService(new JsonProjectRepository(), new FfprobeMediaReader(tools));
            var session = await service.CreateAsync(Path.Combine(root, "workspace"), "large fonts");
            session = await service.ImportMediaAsync(session, song, MediaKind.Audio);
            LyricLine Line(int start) => new(Guid.NewGuid(), "Sing", AnnotationType.None, null, null,
                new(ProjectTime.FromSeconds(start), ProjectTime.FromSeconds(start + 1)),
                [new(Guid.NewGuid(), [new(0, 4)], "Sing", null,
                    new(ProjectTime.FromSeconds(start), ProjectTime.FromSeconds(start + 1)), null, true)],
                ReviewState.Confirmed, null);
            var project = session.Project with
            {
                Lyrics = new(LyricLanguage.English, [Line(0), Line(1)]),
                SubtitleStyle = session.Project.SubtitleStyle with
                { PrimaryFontSize = 240, AnnotationFontSize = 80, OutlineWidth = 0, ShadowOffset = 0 },
                ExportPreset = new(320, 180, 24, 2000, 192),
            };
            string output = Path.Combine(root, "large.mp4");
            await new VideoExportService(tools).ExportAsync(session.RootPath, project, output);
            var details = await new FfprobeMediaReader(tools).ReadDetailsAsync(output);
            Assert.Equal(320, details.Width); Assert.Equal(180, details.Height);
            Assert.True(details.HasAudio); Assert.InRange(details.Duration.TotalSeconds, 1.95, 2.1);
            string image = Path.Combine(root, "frame.png");
            await MediaProcess.RunAsync(tools.FfmpegPath,
                ["-nostdin", "-v", "error", "-y", "-i", output, "-frames:v", "1", "-update", "1", image]);
            using var stream = File.OpenRead(image);
            var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var bitmap = new FormatConvertedBitmap(decoded.Frames[0], PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[320 * 180 * 4]; bitmap.CopyPixels(pixels, 320 * 4, 0);
            // Convert the black background into transparent pixels to compare glyph bounds.
            for (int i = 0; i < pixels.Length; i += 4)
                pixels[i + 3] = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
            var actual = new RenderedFrame(320, 180, 320 * 4, pixels);
            await using var renderer = new WpfSubtitleFrameRenderer(project);
            var expected = await renderer.RenderAsync(ProjectTime.Zero, 320, 180);
            var expectedBounds = Bounds(expected); var actualBounds = Bounds(actual);
            Assert.InRange(Math.Abs(expectedBounds.Top - actualBounds.Top), 0, 2);
            Assert.InRange(Math.Abs(expectedBounds.Bottom - actualBounds.Bottom), 0, 2);
            Assert.True(actualBounds.Top < 180 * .7);
            Assert.True(actualBounds.Bottom > 180 * .85);
            Save(expected, "two-large-rows-shared-render.png");
            string? snapshots = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
            if (snapshots is not null) File.Copy(image, Path.Combine(snapshots, "two-large-rows-real-mp4.png"), true);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(1920, 1080, "Sing", false)]
    [InlineData(1440, 1080, "歌声", false)]
    [InlineData(1080, 1080, "歌", true)]
    [InlineData(1080, 1920, "歌", true)]
    [InlineData(1920, 1080, "歌", true)]
    public async Task LargeFontsGrowAboveRetiredRegionAndLegacyRatioHasNoEffect(int width, int height, string text, bool japanese)
    {
        var unit = new LyricUnit(Guid.NewGuid(), [new(0, text.Length)], japanese ? "うた" : text, null,
            new(ProjectTime.FromSeconds(1), ProjectTime.FromSeconds(4)), null, true);
        var line = new LyricLine(Guid.NewGuid(), text, AnnotationType.None, null, null,
            unit.Timing, [unit], ReviewState.Confirmed, null);
        if (japanese) line = line with { Presentation = LanguagePresentation.BuildJapanese(line) };
        var project = KaraokeProject.Create("font-driven", DateTimeOffset.UnixEpoch) with
        {
            Lyrics = new(japanese ? LyricLanguage.Japanese : LyricLanguage.Auto, [line]),
        };
        var style = project.SubtitleStyle with { PrimaryFontSize = 96, AnnotationFontSize = 42, OutlineWidth = 0, ShadowOffset = 0 };
        await using var smallRenderer = new WpfSubtitleFrameRenderer(project with { SubtitleStyle = style });
        var large = style with { PrimaryFontSize = 256, AnnotationFontSize = 112 };
        await using var largeRenderer = new WpfSubtitleFrameRenderer(project with { SubtitleStyle = large });
        await using var legacyRenderer = new WpfSubtitleFrameRenderer(project with { SubtitleStyle = large with { SafeAreaRatio = .05 } });
        var smallFrame = await smallRenderer.RenderAsync(ProjectTime.FromSeconds(1), width, height);
        var largeFrame = await largeRenderer.RenderAsync(ProjectTime.FromSeconds(1), width, height);
        var legacyFrame = await legacyRenderer.RenderAsync(ProjectTime.FromSeconds(1), width, height);
        var smallBounds = Bounds(smallFrame);
        var largeBounds = Bounds(largeFrame);
        Assert.True(largeBounds.Height > smallBounds.Height * 1.7);
        Assert.True(largeBounds.Top < height * .7, "Font growth must extend above the retired bottom 30% clip.");
        Assert.InRange(largeBounds.Top, 1, height - 1);
        Assert.True(largeBounds.Bottom < height - 1);
        Assert.Equal(largeFrame.BgraPixels.ToArray(), legacyFrame.BgraPixels.ToArray());
        Save(largeFrame, $"font-driven-{width}-{height}-{japanese}.png");
    }

    private static (int Top, int Bottom, int Height) Bounds(RenderedFrame frame)
    {
        int top = frame.Height, bottom = -1;
        var pixels = frame.BgraPixels.Span;
        for (int y = 0; y < frame.Height; y++)
        for (int x = 0; x < frame.Width; x++)
            if (pixels[y * frame.Stride + x * 4 + 3] > 64) { top = Math.Min(top, y); bottom = y; }
        Assert.True(bottom > top);
        return (top, bottom, bottom - top + 1);
    }

    private static void Save(RenderedFrame frame, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.BgraPixels.ToArray(), frame.Stride);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
}
