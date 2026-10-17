using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Infrastructure.Export;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Infrastructure.Workspaces;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class ImageCoverExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "KaraokeFilmMaker.ImageCoverTests", Guid.NewGuid().ToString("N"));
    private readonly MediaToolchain _tools = MediaToolchain.Find();

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(960, 720)]
    [InlineData(1440, 1080)]
    [InlineData(720, 720)]
    [InlineData(1080, 1080)]
    [InlineData(720, 1280)]
    [InlineData(1080, 1920)]
    public async Task ImageCoversEveryCanvasWithCenteredProportionalCrop(int width, int height)
    {
        Directory.CreateDirectory(_root);
        string imagePath = Path.Combine(_root, "wide-center-marker.png");
        // A wide image has green outer thirds, a red central square, and a small white center square.
        // The center marker verifies both crop alignment and equal horizontal/vertical scaling.
        await MediaProcess.RunAsync(_tools.FfmpegPath,
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=lime:s=480x160",
                "-vf", "drawbox=x=160:y=0:w=160:h=160:color=red:t=fill,drawbox=x=220:y=60:w=40:h=40:color=white:t=fill",
                "-frames:v", "1", "-update", "1", imagePath]);
        var session = await CreateSongProjectAsync();
        var service = new WorkspaceService(new JsonProjectRepository(), new FfprobeMediaReader(_tools));
        session = await service.ImportMediaAsync(session, imagePath, MediaKind.Image);
        var project = session.Project with
        {
            ExportPreset = session.Project.ExportPreset with { Width = width, Height = height, FramesPerSecond = 24 },
        };

        string output = Path.Combine(session.RootPath, "exports", "cover.mp4");
        await new VideoExportService(_tools).ExportAsync(session.RootPath, project, output);
        var details = await new FfprobeMediaReader(_tools).ReadDetailsAsync(output);
        Assert.Equal(width, details.Width);
        Assert.Equal(height, details.Height);
        Assert.Equal(24, details.FrameRate);
        Assert.True(details.HasAudio);
        Assert.InRange(details.Duration.TotalSeconds, 0.2, 0.4);

        var frame = await ReadFrameAsync(output);
        AssertWhite(Pixel(frame, width / 2, height / 2));
        int inside = (int)(height * 0.10);
        int outside = (int)(height * 0.15);
        foreach (int sign in new[] { -1, 1 })
        {
            AssertWhite(Pixel(frame, width / 2 + sign * inside, height / 2));
            AssertWhite(Pixel(frame, width / 2, height / 2 + sign * inside));
            AssertRed(Pixel(frame, width / 2 + sign * outside, height / 2));
            AssertRed(Pixel(frame, width / 2, height / 2 + sign * outside));
        }
        // No letterbox is introduced at any edge; landscape edges show the original green outer thirds.
        foreach (int x in new[] { 8, width - 9 })
        {
            foreach (int y in new[] { 8, height - 9 })
            {
                byte[] pixel = Pixel(frame, x, y);
                Assert.True(pixel[1] > 150 || pixel[2] > 150, $"Image did not cover ({x}, {y}).");
                if (width > height) { AssertGreen(pixel); }
                else { AssertRed(pixel); }
            }
        }
    }

    [Fact]
    public async Task PortraitCanvasKeepsVideoContainingBehavior()
    {
        Directory.CreateDirectory(_root);
        string videoPath = Path.Combine(_root, "wide-blue.mp4");
        await MediaProcess.RunAsync(_tools.FfmpegPath,
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=24:d=0.3",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", videoPath]);
        var session = await CreateSongProjectAsync();
        var service = new WorkspaceService(new JsonProjectRepository(), new FfprobeMediaReader(_tools));
        session = await service.ImportMediaAsync(session, videoPath, MediaKind.Video);
        var project = session.Project with
        {
            ExportPreset = session.Project.ExportPreset with { Width = 180, Height = 320, FramesPerSecond = 24 },
            VideoClips = session.Project.VideoClips.Select(clip => clip with { IsMuted = true }).ToArray(),
        };
        string output = Path.Combine(session.RootPath, "exports", "video-contain.mp4");
        await new VideoExportService(_tools).ExportAsync(session.RootPath, project, output);
        var frame = await ReadFrameAsync(output);
        byte[] center = Pixel(frame, 90, 160);
        Assert.True(center[0] > 150 && center[1] < 60 && center[2] < 60);
        foreach (int y in new[] { 8, 312 })
        {
            byte[] edge = Pixel(frame, 90, y);
            Assert.True(edge[0] < 20 && edge[1] < 20 && edge[2] < 20);
        }
    }

    private async Task<WorkspaceSession> CreateSongProjectAsync()
    {
        string songPath = Path.Combine(_root, "song.wav");
        await MediaProcess.RunAsync(_tools.FfmpegPath,
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=0.25",
                "-ar", "48000", "-ac", "2", songPath]);
        var service = new WorkspaceService(new JsonProjectRepository(), new FfprobeMediaReader(_tools));
        var session = await service.CreateAsync(Path.Combine(_root, "workspace"), "画幅与图片填充");
        return await service.ImportMediaAsync(session, songPath, MediaKind.Audio);
    }

    private async Task<BitmapSource> ReadFrameAsync(string output)
    {
        string framePath = Path.Combine(_root, "frame.png");
        await MediaProcess.RunAsync(_tools.FfmpegPath,
            ["-nostdin", "-v", "error", "-y", "-i", output, "-frames:v", "1", "-update", "1", framePath]);
        using var stream = File.OpenRead(framePath);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Pixel(BitmapSource bitmap, int x, int y)
    {
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static void AssertWhite(byte[] pixel) => Assert.True(pixel[0] > 220 && pixel[1] > 220 && pixel[2] > 220);
    private static void AssertRed(byte[] pixel) => Assert.True(pixel[2] > 150 && pixel[0] < 60 && pixel[1] < 60);
    private static void AssertGreen(byte[] pixel) => Assert.True(pixel[1] > 150 && pixel[0] < 60 && pixel[2] < 60);

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
