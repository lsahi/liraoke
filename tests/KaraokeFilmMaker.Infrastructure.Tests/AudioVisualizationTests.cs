using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Audio;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Rendering;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class AudioVisualizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Liraoke.SpectrumTests", Guid.NewGuid().ToString("N"));
    private readonly MediaToolchain _tools = MediaToolchain.Find();

    [Fact]
    public async Task ToneHasCorrectSpectralRegionAndCacheIsReusableWithoutDecoder()
    {
        string path = await CreateAudioAsync("sine=frequency=440:sample_rate=11025:duration=1.2");
        var data = await new AudioVisualizationCache(_tools).GetAsync(path, _root);
        Assert.InRange(data.DurationSeconds, 1.199, 1.201);
        Assert.Equal(36, data.FrameCount);
        var values = new float[32];
        data.Sample(ProjectTime.FromSeconds(0.5), values);
        int maximum = Array.IndexOf(values, values.Max());
        double low = 40 * Math.Pow(125, maximum / 32d);
        double high = 40 * Math.Pow(125, (maximum + 1) / 32d);
        Assert.InRange(440, low - 15, high + 15);
        Assert.True(values.Max() > 0.4);
        Assert.True(values[^1] < 0.01);

        var noDecoder = new AudioVisualizationCache(new MediaToolchain("does-not-exist.exe", "does-not-exist.exe"));
        AudioVisualizationData cached = await noDecoder.GetAsync(path, _root);
        var comparison = new float[32];
        cached.Sample(ProjectTime.FromSeconds(0.5), comparison);
        Assert.Equal(values, comparison);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "cache", "audio-visualization"), "*.tmp"));
    }

    [Fact]
    public async Task SilenceDoesNotCreateMotionAndOriginalTimeIsIndependentOfSeekOrder()
    {
        string path = await CreateAudioAsync("anullsrc=r=11025:cl=mono", "0.5");
        var data = await new AudioVisualizationCache(_tools).GetAsync(path, _root);
        var values = new float[32];
        foreach (double time in new[] { 0.0, 0.4, 0.1, 2.0 })
        {
            data.Sample(ProjectTime.FromSeconds(time), values);
            Assert.All(values, value => Assert.Equal(0, value));
        }
    }

    [Fact]
    public async Task CorruptCacheRebuildsAndChangedSourceDoesNotReuseOldFrames()
    {
        string path = await CreateAudioAsync("sine=frequency=330:sample_rate=11025:duration=0.4");
        var cache = new AudioVisualizationCache(_tools);
        await cache.GetAsync(path, _root);
        string file = Assert.Single(Directory.GetFiles(Path.Combine(_root, "cache", "audio-visualization"), "*.spectrum"));
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        var repaired = await cache.GetAsync(path, _root);
        Assert.Equal(12, repaired.FrameCount);
        Assert.True(new FileInfo(file).Length > 3);
        await CreateAudioAsync("anullsrc=r=11025:cl=mono", "0.7");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        var changed = await cache.GetAsync(path, _root);
        Assert.InRange(changed.DurationSeconds, 0.699, 0.701);
        var values = new float[32];
        changed.Sample(ProjectTime.FromSeconds(0.3), values);
        Assert.All(values, value => Assert.Equal(0, value));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "cache", "audio-visualization"), "*.spectrum").Length);
    }

    [Fact]
    public async Task PreCanceledRequestDoesNotCreateCacheDirectories()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AudioVisualizationCache(_tools).GetAsync(Path.Combine(_root, "missing.wav"), _root, cancellation.Token));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task CancellationDuringDecodeCleansTemporaryCacheAndNeverPublishesPartialFrames()
    {
        string path = await CreateAudioAsync("sine=frequency=220:sample_rate=11025:duration=600");
        using var cancellation = new CancellationTokenSource();
        Task<AudioVisualizationData> analysis = new AudioVisualizationCache(_tools).GetAsync(path, _root, cancellation.Token);
        string directory = Path.Combine(_root, "cache", "audio-visualization");
        var timeout = Stopwatch.StartNew();
        while ((!Directory.Exists(directory) || Directory.GetFiles(directory, "*.tmp").Length == 0) && !analysis.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(5);
        }
        Assert.False(analysis.IsCompleted, "The fixture must cancel while analysis is still in progress.");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analysis);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task DecoderFailureDoesNotLeaveAnApparentlyValidCache()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "invalid.wav");
        await File.WriteAllTextAsync(path, "not a wave file");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AudioVisualizationCache(_tools).GetAsync(path, _root));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "cache", "audio-visualization")));
    }

    [Fact]
    public async Task RendererSwitchControlsTransparencyAndSameSeekProducesIdenticalPixels()
    {
        byte[] frames = Enumerable.Repeat((byte)220, 32 * 30).ToArray();
        var data = new AudioVisualizationData(1, frames);
        var project = KaraokeProject.Create("Spectrum", DateTimeOffset.UtcNow);
        await using var disabled = new WpfSubtitleFrameRenderer(project, data);
        var disabledFrame = await disabled.RenderAsync(ProjectTime.Zero, 640, 360);
        Assert.All(disabledFrame.BgraPixels.ToArray(), value => Assert.Equal(0, value));
        await using var enabled = new WpfSubtitleFrameRenderer(project with { AudioVisualizationEnabled = true }, data);
        var rendered = await enabled.RenderAsync(ProjectTime.FromSeconds(0.3), 640, 360);
        Assert.Contains(rendered.BgraPixels.ToArray(), value => value > 0);
        var ended = await enabled.RenderAsync(ProjectTime.FromSeconds(1), 640, 360);
        Assert.All(ended.BgraPixels.ToArray(), value => Assert.Equal(0, value));
        var repeated = await enabled.RenderAsync(ProjectTime.FromSeconds(0.3), 640, 360);
        Assert.Equal(rendered.BgraPixels.ToArray(), repeated.BgraPixels.ToArray());
        for (int y = 0; y < 360; y++)
        {
            if (y >= 278 && y <= 352) { continue; }
            Assert.All(rendered.BgraPixels.Slice(y * rendered.Stride, rendered.Stride).ToArray(), value => Assert.Equal(0, value));
        }
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(960, 720)]
    [InlineData(720, 720)]
    [InlineData(720, 1280)]
    public async Task SpectrumIsAnchoredBottomLeftAndPreviewPainterMatchesExportForEveryAspectRatio(int width, int height)
    {
        var data = new AudioVisualizationData(1, Enumerable.Repeat((byte)255, 32 * 30).ToArray());
        var time = ProjectTime.FromSeconds(0.3);
        var project = KaraokeProject.Create("Bottom-left spectrum", DateTimeOffset.UtcNow) with { AudioVisualizationEnabled = true };
        await using var renderer = new WpfSubtitleFrameRenderer(project, data);
        var exported = await renderer.RenderAsync(time, width, height);
        byte[] pixels = exported.BgraPixels.ToArray();

        // The preview control uses this exact painter at the video viewport's size.
        byte[] previewPixels = await RenderPreviewPainterAsync(data, time, width, height);
        Assert.Equal(previewPixels, pixels);

        int left = width, right = -1, top = height, bottom = -1;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (pixels[y * exported.Stride + x * 4 + 3] == 0) { continue; }
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        double shortSide = Math.Min(width, height);
        double inset = shortSide * 0.025;
        double spectrumWidth = width * 0.52;
        double slotPadding = spectrumWidth / AudioVisualizationData.SpectrumBandCount * 0.19;
        Assert.InRange(left, inset + slotPadding - 2, inset + slotPadding + 2);
        Assert.InRange(right, inset + spectrumWidth - slotPadding - 2, inset + spectrumWidth - slotPadding + 2);
        Assert.InRange(top, height - inset - shortSide * 0.20 - 2, height - inset - shortSide * 0.20 + 2);
        Assert.InRange(bottom, height - inset - 2, height - inset + 2);
        Assert.True(right < width * 0.57, "Spectrum must remain in the left part of every viewport.");
        Assert.True(top > height * 0.75, "Spectrum must rise from the bottom, not the middle of the frame.");
    }

    [Fact]
    public async Task EnabledRendererWithoutReadyDataRemainsTransparent()
    {
        var project = KaraokeProject.Create("Not analyzed", DateTimeOffset.UtcNow) with { AudioVisualizationEnabled = true };
        await using var renderer = new WpfSubtitleFrameRenderer(project);
        var rendered = await renderer.RenderAsync(ProjectTime.Zero, 320, 180);
        Assert.All(rendered.BgraPixels.ToArray(), value => Assert.Equal(0, value));
    }

    private async Task<string> CreateAudioAsync(string source, string? duration = null)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "source.wav");
        var arguments = new List<string> { "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", source };
        if (duration is not null) { arguments.AddRange(["-t", duration]); }
        arguments.AddRange(["-c:a", "pcm_s16le", path]);
        await MediaProcess.RunAsync(_tools.FfmpegPath, arguments);
        return path;
    }

    private static Task<byte[]> RenderPreviewPainterAsync(AudioVisualizationData data, ProjectTime time, int width, int height)
    {
        var result = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var visual = new DrawingVisual();
                using (DrawingContext context = visual.RenderOpen())
                { new WpfAudioVisualizationPainter().Draw(context, data, time, width, height); }
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                // Export uses straight alpha while WPF preview uses premultiplied alpha.
                for (int index = 0; index < pixels.Length; index += 4)
                {
                    int alpha = pixels[index + 3];
                    if (alpha is <= 0 or >= 255) { continue; }
                    for (int channel = 0; channel < 3; channel++)
                    { pixels[index + channel] = (byte)Math.Min(255, (pixels[index + channel] * 255 + alpha / 2) / alpha); }
                }
                result.TrySetResult(pixels);
            }
            catch (Exception error) { result.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
}
