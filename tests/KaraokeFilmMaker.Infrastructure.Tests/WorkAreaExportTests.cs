using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Export;
using KaraokeFilmMaker.Infrastructure.Media;
using NAudio.Wave;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class WorkAreaExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Liraoke.WorkAreaExport", Guid.NewGuid().ToString("N"));
    private readonly MediaToolchain _tools = MediaToolchain.Find();

    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(60)]
    public async Task TrimmedOutputKeepsVideoAudioLyricsAndImageOnTheSameClock(int fps)
    {
        var project = await CreateProjectAsync(fps);
        var original = project;
        string output = Path.Combine(_root, "cropped.mp4");
        await new VideoExportService(_tools).ExportAsync(_root, project, output);
        var details = await new FfprobeMediaReader(_tools).ReadDetailsAsync(output);
        Assert.InRange(details.Duration.TotalSeconds, 1.49, 1.55);
        Assert.Equal(fps, details.FrameRate);
        Assert.True(details.HasAudio);
        byte[] first = await FrameAsync(output, 0.1);
        AssertBlue(Pixel(first, 160, 30)); // Original source 1.1 s, not source 0.35 s (red).
        byte[] lyric = await FrameAsync(output, 0.5);
        int yellow = 0;
        for (int y = 125; y < 180; y++)
        for (int x = 0; x < 320; x++)
        {
            byte[] pixel = Pixel(lyric, x, y);
            if (pixel[2] > 140 && pixel[1] > 110 && pixel[0] < 130) { yellow++; }
        }
        Assert.True(yellow > 5, "Subtitle highlighting must use the original 1.75 s clock, not output 0.5 s.");
        byte[] gap = Pixel(await FrameAsync(output, 0.85), 160, 30);
        Assert.All(gap.Take(3), value => Assert.True(value < 20));
        byte[] image = Pixel(await FrameAsync(output, 1.2), 160, 30);
        Assert.True(image[1] > 180 && image[0] < 30 && image[2] < 30);

        string decoded = Path.Combine(_root, "decoded.wav");
        await MediaProcess.RunAsync(_tools.FfmpegPath,
            ["-v", "error", "-y", "-i", output, "-vn", "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "1", decoded]);
        using var wave = new WaveFileReader(decoded);
        var samples = new float[4800];
        int read = wave.ToSampleProvider().Read(samples);
        Assert.True(Math.Sqrt(samples.Take(read).Average(value => value * value)) > 0.1,
            "Export audio must start at cropped song time, not the silent original intro.");
        Assert.Equal(original, project);
        Assert.Empty(Directory.GetFiles(_root, "*.partial.mp4"));
    }

    [Fact]
    public async Task TrimmedSpectrumUsesOriginalSongTimeAndFullLengthCanBeRestored()
    {
        var project = await CreateProjectAsync(24);
        project = project with { Lyrics = project.Lyrics with { Lines = [] }, VideoClips = [], AudioVisualizationEnabled = true };
        string cropped = Path.Combine(_root, "spectrum-cropped.mp4"), full = Path.Combine(_root, "spectrum-full.mp4");
        var exporter = new VideoExportService(_tools);
        await exporter.ExportAsync(_root, project, cropped);
        await exporter.ExportAsync(_root, project with { WorkArea = null }, full);
        var croppedFrame = await FrameAsync(cropped, 0.1);
        var fullFrame = await FrameAsync(full, 0.1);
        int VisiblePixels(byte[] data) => data.Where((value, index) => index % 4 != 3 && value > 60).Count();
        Assert.True(VisiblePixels(croppedFrame) > VisiblePixels(fullFrame) + 20);
        Assert.InRange((await new FfprobeMediaReader(_tools).ReadDetailsAsync(full)).Duration.TotalSeconds, 3.99, 4.1);
    }

    private async Task<KaraokeProject> CreateProjectAsync(int fps)
    {
        Directory.CreateDirectory(_root);
        using (var wave = new WaveFileWriter(Path.Combine(_root, "song.wav"), new WaveFormat(48000, 16, 1)))
        {
            for (int sample = 0; sample < 48000 * 4; sample++)
            { wave.WriteSample(sample < 48000 ? 0 : (float)(0.4 * Math.Sin(2 * Math.PI * 440 * sample / 48000))); }
        }
        await MediaProcess.RunAsync(_tools.FfmpegPath,
            ["-v", "error", "-y", "-f", "lavfi", "-i", $"color=c=red:s=320x180:r={fps}:d=2",
                "-vf", "drawbox=x=0:y=0:w=iw:h=ih:color=blue:t=fill:enable='gte(t,0.9)'",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", Path.Combine(_root, "video.mp4")]);
        await MediaProcess.RunAsync(_tools.FfmpegPath,
            ["-v", "error", "-y", "-f", "lavfi", "-i", "color=c=lime:s=320x180",
                "-frames:v", "1", "-update", "1", Path.Combine(_root, "image.png")]);
        var song = new MediaAsset(Guid.NewGuid(), "song", "song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(4));
        var video = new MediaAsset(Guid.NewGuid(), "video", "video.mp4", MediaKind.Video, ProjectDuration.FromSeconds(2));
        var image = new MediaAsset(Guid.NewGuid(), "image", "image.png", MediaKind.Image, ProjectDuration.Zero);
        var lyrics = new LyricImportService().Parse("TEST", ".txt", LyricLanguage.English);
        var line = LyricTimingService.Retiming(lyrics.Lines[0], new(ProjectTime.FromSeconds(1), ProjectTime.FromSeconds(2)));
        return KaraokeProject.Create("裁切同步", DateTimeOffset.UtcNow) with
        {
            MasterAudioAssetId = song.Id, MediaAssets = [song, video, image],
            WorkArea = new(ProjectTime.FromSeconds(1.25), ProjectTime.FromSeconds(2.75)),
            Lyrics = lyrics with { Lines = [line] },
            VideoClips =
            [
                new(Guid.NewGuid(), image.Id, ProjectTime.Zero, ProjectTime.Zero, ProjectDuration.FromSeconds(0.5), true),
                new(Guid.NewGuid(), video.Id, ProjectTime.FromSeconds(0.5), ProjectTime.FromSeconds(0.25), ProjectDuration.FromSeconds(1.5), true),
                new(Guid.NewGuid(), image.Id, ProjectTime.FromSeconds(2.25), ProjectTime.Zero, ProjectDuration.FromSeconds(3), true),
            ],
            ExportPreset = new(320, 180, fps, 2000, 192),
            SubtitleStyle = KaraokeProject.Create("style", DateTimeOffset.UtcNow).SubtitleStyle with
            { FontFamily = "Arial", PrimaryFontSize = 96, OutlineWidth = 0, ShadowOffset = 0 },
        };
    }

    private async Task<byte[]> FrameAsync(string video, double seconds)
    {
        string target = Path.Combine(_root, Guid.NewGuid() + ".png");
        await MediaProcess.RunAsync(_tools.FfmpegPath,
            ["-v", "error", "-y", "-ss", seconds.ToString(CultureInfo.InvariantCulture), "-i", video,
                "-frames:v", "1", "-update", "1", target]);
        string? snapshotDirectory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (snapshotDirectory is not null)
        {
            Directory.CreateDirectory(snapshotDirectory);
            File.Copy(target, Path.Combine(snapshotDirectory, $"{Path.GetFileNameWithoutExtension(video)}-{seconds:0.00}.png"), true);
        }
        using var stream = File.OpenRead(target);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var pixels = new byte[320 * 180 * 4]; bitmap.CopyPixels(pixels, 320 * 4, 0); return pixels;
    }
    private static byte[] Pixel(byte[] data, int x, int y) => data.Skip((y * 320 + x) * 4).Take(4).ToArray();
    private static void AssertBlue(byte[] pixel) => Assert.True(pixel[0] > 180 && pixel[1] < 30 && pixel[2] < 30);
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
}
