using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Infrastructure.Rendering;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class VisualizationVarietyTests
{
    public static IEnumerable<object[]> StylesAndFormats => Enum.GetValues<AudioVisualizationStyle>()
        .SelectMany(style => new[] { (640, 360), (480, 360), (360, 360), (360, 640) }
            .Select(size => new object[] { style, size.Item1, size.Item2 }));

    [Theory]
    [MemberData(nameof(StylesAndFormats))]
    public async Task AllStylesMatchPreviewExportSeekAndSilence(AudioVisualizationStyle style, int width, int height)
    {
        var data = new AudioVisualizationData(3, Enumerable.Repeat((byte)255, 32 * 90).ToArray());
        var project = KaraokeProject.Create("可视化", DateTimeOffset.UtcNow) with { AudioVisualizationEnabled = true, AudioVisualizationStyle = style };
        await using var renderer = new WpfSubtitleFrameRenderer(project, data);
        var time = ProjectTime.FromSeconds(0.3);
        var frame = await renderer.RenderAsync(time, width, height);
        byte[] pixels = frame.BgraPixels.ToArray();
        Assert.Contains(pixels, value => value > 0);
        Assert.Equal(await Preview(data, time, style, width, height), pixels);
        var end = await renderer.RenderAsync(ProjectTime.FromSeconds(3), width, height);
        Assert.All(end.BgraPixels.ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(pixels, (await renderer.RenderAsync(time, width, height)).BgraPixels.ToArray());
        await using var silent = new WpfSubtitleFrameRenderer(project, new AudioVisualizationData(3, new byte[32 * 90]));
        Assert.All((await silent.RenderAsync(time, width, height)).BgraPixels.ToArray(), value => Assert.Equal(0, value));
        await using var disabled = new WpfSubtitleFrameRenderer(project with { AudioVisualizationEnabled = false }, data);
        Assert.All((await disabled.RenderAsync(time, width, height)).BgraPixels.ToArray(), value => Assert.Equal(0, value));

        int left = width, right = -1, top = height, bottom = -1, maxAlpha = 0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * frame.Stride + x * 4;
            if (pixels[i + 3] == 0) { continue; }
            left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            maxAlpha = Math.Max(maxAlpha, pixels[i + 3]);
            if (style is AudioVisualizationStyle.CenterWhite or AudioVisualizationStyle.RotatingRing or AudioVisualizationStyle.Fireworks)
            { Assert.Equal(255, pixels[i]); Assert.Equal(255, pixels[i + 1]); Assert.Equal(255, pixels[i + 2]); }
        }
        Assert.InRange(maxAlpha, 1, 254);
        if (style == AudioVisualizationStyle.BottomDense)
        {
            Assert.InRange(left, 0, 2); Assert.InRange(right, width - 3, width - 1);
            Assert.Equal(height - 1, bottom); Assert.True(top >= height - Math.Min(width, height) * 0.23);
        }
        if (style == AudioVisualizationStyle.CenterWhite)
        {
            Assert.InRange(left, 0, 2); Assert.InRange(right, width - 3, width - 1);
            Assert.InRange((top + bottom + 1) / 2.0, height / 2.0 - 1, height / 2.0 + 1);
            Assert.InRange(bottom - top + 1, height * 0.5 - 2, height * 0.5 + 2);
            Assert.InRange(maxAlpha, 1, 96);
        }
        if (style == AudioVisualizationStyle.RotatingRing)
        {
            Assert.InRange((left + right + 1) / 2.0, width / 2.0 - 2, width / 2.0 + 2);
            Assert.InRange((top + bottom + 1) / 2.0, height / 2.0 - 2, height / 2.0 + 2);
            Assert.Equal(0, pixels[(height / 2 * width + width / 2) * 4 + 3]);
            double shortSide = Math.Min(width, height);
            Assert.InRange(bottom - top + 1, shortSide * 0.7168 - 3, shortSide * 0.7168 + 3);
            Assert.InRange(right - left + 1, shortSide * 0.7168 - 3, shortSide * 0.7168 + 3);
            // No part of a bar may intrude into the enlarged inner circle (allow edge AA).
            double inner = shortSide * 0.2024 - 2;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double dx = x + 0.5 - width / 2.0, dy = y + 0.5 - height / 2.0;
                if (dx * dx + dy * dy < inner * inner) { Assert.Equal(0, pixels[(y * width + x) * 4 + 3]); }
            }
        }
        Save(frame, $"visualization-{style}-{width}-{height}.png");
    }

    [Fact]
    public async Task CenterStyleHas256FaintBarsWithEqualWidthAndGap()
    {
        var project = KaraokeProject.Create("细线", DateTimeOffset.UtcNow) with
        { AudioVisualizationEnabled = true, AudioVisualizationStyle = AudioVisualizationStyle.CenterWhite };
        await using var renderer = new WpfSubtitleFrameRenderer(project,
            new AudioVisualizationData(1, Enumerable.Repeat((byte)255, 32 * 30).ToArray()));
        var frame = await renderer.RenderAsync(ProjectTime.FromSeconds(0.3), 1280, 720);
        byte[] pixels = frame.BgraPixels.ToArray();
        int bars = 0, run = 0, widest = 0, maxAlpha = 0, alphaSum = 0;
        for (int x = 0; x < 1280; x++)
        {
            int alpha = pixels[(360 * 1280 + x) * 4 + 3]; maxAlpha = Math.Max(maxAlpha, alpha); alphaSum += alpha;
            if (alpha > 0) { if (run == 0) { bars++; } widest = Math.Max(widest, ++run); }
            else { run = 0; }
        }
        Assert.Equal(256, bars); Assert.InRange(widest, 1, 3); Assert.Equal(96, maxAlpha);
        // Integrate edge antialiasing: white columns occupy exactly half of the horizontal period.
        Assert.InRange(alphaSum / (1280.0 * maxAlpha), 0.499, 0.501);
    }

    [Fact]
    public async Task VariedFrequencyInputProducesDistinctStylePreviews()
    {
        byte[] frames = Enumerable.Range(0, 32 * 30 * 3).Select(i =>
            (byte)(35 + 190 * Math.Exp(-(i % 32) / 35.0) * (0.3 + 0.7 * Math.Abs(Math.Sin(i % 32 * 0.43 + 0.4))))).ToArray();
        var data = new AudioVisualizationData(3, frames);
        var pictures = new List<byte[]>();
        foreach (var style in Enum.GetValues<AudioVisualizationStyle>())
        {
            var project = KaraokeProject.Create("样式预览", DateTimeOffset.UtcNow) with { AudioVisualizationEnabled = true, AudioVisualizationStyle = style };
            await using var renderer = new WpfSubtitleFrameRenderer(project, data);
            var frame = await renderer.RenderAsync(ProjectTime.FromSeconds(0.3), 1280, 720);
            byte[] pixels = frame.BgraPixels.ToArray();
            Assert.All(pictures, previous => Assert.False(previous.SequenceEqual(pixels)));
            pictures.Add(pixels); Save(frame, $"style-demo-{style}.png");
        }
    }

    [Fact]
    public async Task AsymmetricRingReturnsToIdenticalPhaseAfterHundredSeconds()
    {
        var data = new AudioVisualizationData(105, Enumerable.Range(0, 32 * 30 * 105).Select(i => (byte)(50 + i % 32 * 6)).ToArray());
        var project = KaraokeProject.Create("旋转", DateTimeOffset.UtcNow) with
        { AudioVisualizationEnabled = true, AudioVisualizationStyle = AudioVisualizationStyle.RotatingRing };
        await using var renderer = new WpfSubtitleFrameRenderer(project, data);
        byte[] first = (await renderer.RenderAsync(ProjectTime.FromSeconds(2), 640, 360)).BgraPixels.ToArray();
        byte[] quarter = (await renderer.RenderAsync(ProjectTime.FromSeconds(27), 640, 360)).BgraPixels.ToArray();
        byte[] full = (await renderer.RenderAsync(ProjectTime.FromSeconds(102), 640, 360)).BgraPixels.ToArray();
        Assert.False(first.SequenceEqual(quarter)); Assert.Equal(first, full);
    }

    [Theory]
    [InlineData(AudioVisualizationStyle.Classic)]
    [InlineData(AudioVisualizationStyle.BottomDense)]
    [InlineData(AudioVisualizationStyle.CenterWhite)]
    [InlineData(AudioVisualizationStyle.RotatingRing)]
    [InlineData(AudioVisualizationStyle.Fireworks)]
    public async Task StylePersistsAndLegacyProjectsDefaultToClassic(AudioVisualizationStyle style)
    {
        string root = Path.Combine(Path.GetTempPath(), "Liraoke.VisualStyles", Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new JsonProjectRepository();
            var project = KaraokeProject.Create("styles", DateTimeOffset.UtcNow) with { AudioVisualizationEnabled = true, AudioVisualizationStyle = style };
            await repository.SaveAsync(root, project);
            var loaded = await repository.LoadAsync(root);
            Assert.Equal(style, loaded.AudioVisualizationStyle); Assert.True(loaded.AudioVisualizationEnabled);
            Assert.Equal(KaraokeProject.CurrentSchemaVersion, loaded.SchemaVersion);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
            var json = JsonSerializer.SerializeToNode(project, options)!.AsObject();
            json.Remove("audioVisualizationStyle"); json["schemaVersion"] = 7;
            // Isolated test workspace, simulating a saved file from 0.4.3.5.
            string path = Directory.GetFiles(root, "*.lrn").SingleOrDefault() ?? Path.Combine(root, "project.kfp");
            await File.WriteAllTextAsync(path, json.ToJsonString());
            var legacy = await repository.LoadAsync(root);
            Assert.Equal(AudioVisualizationStyle.Classic, legacy.AudioVisualizationStyle);
            Assert.True(legacy.AudioVisualizationEnabled); Assert.Equal(KaraokeProject.CurrentSchemaVersion, legacy.SchemaVersion);
            Assert.Equal(project.Id, legacy.Id);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    private static Task<byte[]> Preview(AudioVisualizationData data, ProjectTime time, AudioVisualizationStyle style, int width, int height)
    {
        var completion = new TaskCompletionSource<byte[]>();
        var thread = new Thread(() =>
        {
            try
            {
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen()) { new WpfAudioVisualizationPainter().Draw(drawing, data, time, width, height, style); }
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                byte[] pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    int alpha = pixels[i + 3]; if (alpha is <= 0 or >= 255) { continue; }
                    for (int c = 0; c < 3; c++) { pixels[i + c] = (byte)Math.Min(255, (pixels[i + c] * 255 + alpha / 2) / alpha); }
                }
                completion.SetResult(pixels);
            }
            catch (Exception e) { completion.SetException(e); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void Save(RenderedFrame frame, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (directory is null) { return; }
        Directory.CreateDirectory(directory);
        byte[] pixels = frame.BgraPixels.ToArray();
        // Composite on black for QA viewers that otherwise display straight-alpha RGB as opaque white.
        for (int i = 0; i < pixels.Length; i += 4)
        {
            for (int c = 0; c < 3; c++) { pixels[i + c] = (byte)((pixels[i + c] * pixels[i + 3] + 127) / 255); }
            pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, pixels, frame.Stride);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
    }
}
