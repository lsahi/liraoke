using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Export;
using KaraokeFilmMaker.Infrastructure.Media;
using NAudio.Wave;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class CreationExportIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Liraoke.CreationExport", Guid.NewGuid().ToString("N"));
    private readonly MediaToolchain _tools = MediaToolchain.Find();

    [Theory]
    [InlineData(AudioVisualizationStyle.Classic)]
    [InlineData(AudioVisualizationStyle.BottomDense)]
    [InlineData(AudioVisualizationStyle.CenterWhite)]
    [InlineData(AudioVisualizationStyle.RotatingRing)]
    [InlineData(AudioVisualizationStyle.Fireworks)]
    public async Task StaticImageWithSpectrumExportsAnimationWithoutChangingTheSong(AudioVisualizationStyle style)
    {
        Directory.CreateDirectory(_root);
        string source = Path.Combine(_root, "song.wav");
        const int rate = 11025;
        using (var wave = new WaveFileWriter(source, new WaveFormat(rate, 16, 1)))
        {
            for (int sample = 0; sample < rate * 1.6; sample++)
            { wave.WriteSample(sample < rate / 2 ? 0 : (float)(0.4 * Math.Sin(2 * Math.PI * 440 * sample / rate))); }
        }
        string image = Path.Combine(_root, "image.png");
        await MediaProcess.RunAsync(_tools.FfmpegPath, ["-v", "error", "-y", "-f", "lavfi", "-i", "color=c=0x0A0F14:s=320x180", "-frames:v", "1", "-update", "1", image]);
        var audio = new MediaAsset(Guid.NewGuid(), "song.wav", "song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(1.6));
        var still = new MediaAsset(Guid.NewGuid(), "image.png", "image.png", MediaKind.Image, ProjectDuration.Zero);
        var project = KaraokeProject.Create("图片频谱导出", DateTimeOffset.UtcNow) with
        {
            MediaAssets = [audio, still], MasterAudioAssetId = audio.Id,
            VideoClips = [new(Guid.NewGuid(), still.Id, ProjectTime.Zero, ProjectTime.Zero, ProjectDuration.FromSeconds(10), true)],
            ExportPreset = new(1280, 720, 30, 4000, 192),
            AudioVisualizationStyle = style,
        };
        var exporter = new VideoExportService(_tools);
        string plain = Path.Combine(_root, "off.mp4"), animated = Path.Combine(_root, "on.mp4");
        await exporter.ExportAsync(_root, project, plain);
        Assert.False(Directory.Exists(Path.Combine(_root, "cache", "audio-visualization")));
        await exporter.ExportAsync(_root, project with { AudioVisualizationEnabled = true }, animated);
        if (style == AudioVisualizationStyle.Fireworks)
        {
            Assert.False(Directory.Exists(Path.Combine(_root, "cache", "audio-visualization")));
            Assert.Equal(await FrameAsync(plain, 1), await FrameAsync(animated, 1));
            var retiredDetails = await new FfprobeMediaReader(_tools).ReadDetailsAsync(animated);
            Assert.True(retiredDetails.HasAudio);
            Assert.InRange(retiredDetails.Duration.TotalSeconds, 1.55, 1.7);
            Assert.Empty(Directory.GetFiles(_root, "*.partial.mp4"));
            return;
        }
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "cache", "audio-visualization"), "*.spectrum"));
        var details = await new FfprobeMediaReader(_tools).ReadDetailsAsync(animated);
        Assert.Equal(1280, details.Width); Assert.Equal(720, details.Height); Assert.True(details.HasAudio);
        Assert.InRange(details.Duration.TotalSeconds, 1.55, 1.7);
        byte[] off = await FrameAsync(plain, 1), silence = await FrameAsync(animated, 0.2), sounding = await FrameAsync(animated, 1);
        Assert.True(ColoredSpectrumPixels(off) < 5);
        Assert.True(ColoredSpectrumPixels(silence) < 5);
        if (style == AudioVisualizationStyle.Classic) { Assert.True(ColoredSpectrumPixels(sounding) > 40); }
        Assert.True(Enumerable.Range(0, sounding.Length / 4).Count(i => sounding[i * 4] > off[i * 4] + 20
            || sounding[i * 4 + 1] > off[i * 4 + 1] + 20 || sounding[i * 4 + 2] > off[i * 4 + 2] + 20) > 15,
            $"{style} must appear in the actual MP4.");
        string offAudio = await MediaProcess.RunAsync(_tools.FfmpegPath, ["-v", "error", "-i", plain, "-map", "0:a:0", "-f", "md5", "-"]);
        string onAudio = await MediaProcess.RunAsync(_tools.FfmpegPath, ["-v", "error", "-i", animated, "-map", "0:a:0", "-f", "md5", "-"]);
        Assert.Equal(offAudio, onAudio);
        Assert.Empty(Directory.GetFiles(_root, "*.partial.mp4"));
    }

    [Fact]
    public async Task CroppedMp4UsesOriginalSongTimeForSixSecondEntrance()
    {
        Directory.CreateDirectory(_root);
        string source = Path.Combine(_root, "intro.wav");
        await MediaProcess.RunAsync(_tools.FfmpegPath, ["-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=22050:cl=mono", "-t", "22", source]);
        var audio = new MediaAsset(Guid.NewGuid(), "intro.wav", "intro.wav", MediaKind.Audio, ProjectDuration.FromSeconds(22));
        var line = new LyricLine(Guid.NewGuid(), "Sing", AnnotationType.None, null, null,
            new(ProjectTime.FromSeconds(20), ProjectTime.FromSeconds(22)), [], ReviewState.Confirmed, null);
        var project = KaraokeProject.Create("前奏", DateTimeOffset.UtcNow) with
        {
            MediaAssets = [audio], MasterAudioAssetId = audio.Id,
            Lyrics = new(LyricLanguage.English, [line]),
            WorkArea = new(ProjectTime.FromSeconds(13.5), ProjectTime.FromSeconds(20.5)),
            ExportPreset = new(1280, 720, 24, 4000, 192),
        };
        string output = Path.Combine(_root, "entrance.mp4");
        await new VideoExportService(_tools).ExportAsync(_root, project, output);
        byte[] before = await FrameAsync(output, 0.2); // Song 13.7: before the six-second lead.
        byte[] lead = await FrameAsync(output, 1); // Song 14.5: lyrics and cue, not blank.
        byte[] counting = await FrameAsync(output, 5); // Song 18.5: cue has two yellow bars.
        byte[] singing = await FrameAsync(output, 6.5); // Song 20: cue disappears, primary stays.
        Assert.True(before.Where((_, i) => i % 4 != 3).Max() < 20);
        Assert.True(lead.Where((_, i) => i % 4 != 3).Max() > 180);
        // Default margin is 12.8px at this extraction size; cue occupies x=13..19, primary starts x=21.
        int CuePixels(byte[] pixels) => Enumerable.Range(0, 180).Sum(y => Enumerable.Range(12, 8)
            .Count(x => pixels[(y * 320 + x) * 4 + 1] > 60));
        Assert.True(CuePixels(counting) > 4);
        Assert.True(CuePixels(singing) < CuePixels(counting));
        Assert.True(singing.Where((_, i) => i % 4 != 3).Max() > 180);
    }

    private static int ColoredSpectrumPixels(byte[] pixels)
    {
        int count = 0;
        for (int y = 135; y < 178; y++)
        for (int x = 2; x < 175; x++)
        {
            int index = (y * 320 + x) * 4;
            if (pixels[index + 1] > 60 && (pixels[index] > 70 || pixels[index + 2] > 70)) { count++; }
        }
        return count;
    }

    private async Task<byte[]> FrameAsync(string video, double seconds)
    {
        string target = Path.Combine(_root, Guid.NewGuid() + ".png");
        await MediaProcess.RunAsync(_tools.FfmpegPath, ["-v", "error", "-y", "-ss", seconds.ToString(CultureInfo.InvariantCulture), "-i", video, "-frames:v", "1", "-vf", "scale=320:180", "-update", "1", target]);
        using var stream = File.OpenRead(target);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var pixels = new byte[320 * 180 * 4]; bitmap.CopyPixels(pixels, 320 * 4, 0); return pixels;
    }

    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
}
