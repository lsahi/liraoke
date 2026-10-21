using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Export;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Rendering;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class AdjacentLineRenderingTests
{
    private static ProjectTime At(double s) => ProjectTime.FromSeconds(s);
    private static KaraokeProject Project()
    {
        var lyrics = new LyricImportService().Parse("春风\n明天\n再唱", ".txt", LyricLanguage.Chinese);
        var timing = new LyricTimingService();
        lyrics = timing.ApplyContinuousUnitMarkers(lyrics, [At(.1), At(.2)], At(2), 0, At(.8), explicitLineEnd: true);
        lyrics = timing.ApplyContinuousUnitMarkers(lyrics, [At(.6), At(.7)], At(2), 1, At(1), onlyUnconfirmed: true, explicitLineEnd: true);
        lyrics = timing.ApplyContinuousUnitMarkers(lyrics, [At(.8), At(.9)], At(2), 2, At(1.1), onlyUnconfirmed: true, explicitLineEnd: true);
        return KaraokeProject.Create("Overlap", DateTimeOffset.UnixEpoch) with { Lyrics = lyrics };
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(960, 720)]
    [InlineData(720, 720)]
    [InlineData(720, 1280)]
    public async Task BothRowsAreActivelyColoredAndSeekingIsDeterministic(int width, int height)
    {
        await using var renderer = new WpfSubtitleFrameRenderer(Project());
        var overlap = await renderer.RenderAsync(At(.75), width, height);
        AssertTwoGoldenRows(overlap.BgraPixels.ToArray(), width, height);
        var after = await renderer.RenderAsync(At(.85), width, height);
        Assert.NotEqual(overlap.BgraPixels.ToArray(), after.BgraPixels.ToArray());
        var seek = await renderer.RenderAsync(At(.75), width, height);
        Assert.Equal(overlap.BgraPixels.ToArray(), seek.BgraPixels.ToArray());
        var end = await renderer.RenderAsync(At(1.1), width, height);
        Assert.All(end.BgraPixels.ToArray().Where((_, i) => i % 4 == 3), a => Assert.Equal(0, a));
        Snapshot(overlap, $"overlap-active-{width}x{height}.png");
    }

    [Fact]
    public async Task CroppedMp4UsesOriginalSongTimeForBothRows()
    {
        string root = Path.Combine(Path.GetTempPath(), "Liraoke.OverlapExport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tools = MediaToolchain.Find();
            await MediaProcess.RunAsync(tools.FfmpegPath, ["-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=22050:cl=mono", "-t", "1.2", Path.Combine(root, "song.wav")]);
            var asset = new MediaAsset(Guid.NewGuid(), "Song.wav", "song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(1.2));
            var project = Project() with { MasterAudioAssetId = asset.Id, MediaAssets = [asset],
                WorkArea = new(At(.6), At(.9)), ExportPreset = new(1280, 720, 24, 6000, 192) };
            string output = Path.Combine(root, "overlap.mp4"), image = Path.Combine(root, "frame.png");
            await new VideoExportService(tools).ExportAsync(root, project, output);
            var details = await new FfprobeMediaReader(tools).ReadDetailsAsync(output);
            Assert.InRange(details.Duration.TotalSeconds, .29, .4); Assert.True(details.HasAudio);
            await MediaProcess.RunAsync(tools.FfmpegPath, ["-v", "error", "-y", "-ss", "0.15", "-i", output, "-frames:v", "1", "-update", "1", image]);
            using var stream = File.OpenRead(image);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            byte[] data = new byte[1280 * 720 * 4]; bitmap.CopyPixels(data, 1280 * 4, 0);
            AssertTwoGoldenRows(data, 1280, 720);
            Assert.Empty(Directory.GetFiles(root, "*.partial.mp4"));
            var snapshots = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
            if (!string.IsNullOrWhiteSpace(snapshots))
            { Directory.CreateDirectory(snapshots); File.Copy(image, Path.Combine(snapshots, "overlap-real-mp4.png"), true); }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void AssertTwoGoldenRows(byte[] pixels, int width, int height)
    {
        var rows = new List<int>();
        for (int y = 0; y < height; y++)
        {
            int count = 0;
            for (int x = 0; x < width; x++)
            { int p = (y * width + x) * 4; if (pixels[p + 2] > 180 && pixels[p + 1] > 130 && pixels[p] < 130) count++; }
            if (count > 4) rows.Add(y);
        }
        Assert.True(rows.Count > 15);
        Assert.True(rows.Zip(rows.Skip(1), (a, b) => b - a).Any(gap => gap > 10), "Both lyric rows must contain active yellow glyphs.");
    }
    private static void Snapshot(RenderedFrame frame, string file)
    {
        var root = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR"); if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.BgraPixels.ToArray(), frame.Stride);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(root, file)); encoder.Save(stream);
    }
}
