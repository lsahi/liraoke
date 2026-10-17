using System.Text.Json;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Infrastructure.Workspaces;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class JpegMetadataImportRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Liraoke.JpegMetadataTests", Guid.NewGuid().ToString("N"));
    private readonly MediaToolchain _tools = MediaToolchain.Find();

    [Theory]
    [InlineData("jpg")]
    [InlineData("jpeg")]
    public async Task JpegWithLargeExifHeaderDetectedAsImage2ImportsAndPreviews(string extension)
    {
        string source = await CreateJpegWithLargeExifHeaderAsync(extension);
        Assert.Equal("image2", await ProbeFormatAsync(source));

        var service = new WorkspaceService(new JsonProjectRepository(), new FfprobeMediaReader(_tools));
        var session = await service.CreateAsync(Path.Combine(_root, "workspace"), "带有 EXIF 的图片");
        session = await service.ImportMediaAsync(session, source, MediaKind.Image);

        var asset = Assert.Single(session.Project.MediaAssets);
        Assert.Equal(MediaKind.Image, asset.Kind);
        Assert.Equal(ProjectDuration.Zero, asset.Duration);
        Assert.Null(session.Project.MasterAudioAssetId);
        Assert.Equal(await File.ReadAllBytesAsync(source),
            await File.ReadAllBytesAsync(Path.Combine(session.RootPath, asset.RelativePath)));
        var clip = Assert.Single(session.Project.VideoClips);
        Assert.Equal(5, clip.Duration.TotalSeconds);
        Assert.Equal(ProjectTime.Zero, clip.SourceStart);
        Assert.True(clip.IsMuted);

        var cache = new ImagePreviewCache(_tools);
        var preview = await cache.PrepareAsync(session.RootPath, asset);
        Assert.Equal(".png", Path.GetExtension(preview.ProxyPath));
        Assert.True(File.Exists(preview.ThumbnailPath));
        Assert.Equal(preview, await cache.PrepareAsync(session.RootPath, asset));
        var details = await new FfprobeMediaReader(_tools).ReadDetailsAsync(preview.ProxyPath);
        Assert.Equal(96, details.Width);
        Assert.Equal(64, details.Height);
        Assert.Equal(ProjectDuration.Zero, details.Duration);
        Assert.False(details.HasAudio);
    }

    [Fact]
    public async Task MjpegVideoRenamedToJpgIsRejectedWithoutKeepingAnImportedCopy()
    {
        Directory.CreateDirectory(_root);
        string video = Path.Combine(_root, "actual-video.avi");
        await MediaProcess.RunAsync(_tools.FfmpegPath,
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=96x64:r=10:d=0.5",
                "-c:v", "mjpeg", video]);
        string renamed = Path.Combine(_root, "looks-like-a-photo.jpg");
        File.Copy(video, renamed);
        Assert.Equal("avi", await ProbeFormatAsync(renamed));

        await AssertRejectedImportAsync(renamed);
    }

    [Fact]
    public async Task ConcatenatedJpegFramesWithStillImageExtensionAreRejected()
    {
        string first = await CreateJpegWithLargeExifHeaderAsync("jpg");
        byte[] frame = await File.ReadAllBytesAsync(first);
        string concatenated = Path.Combine(_root, "two-frames.jpg");
        await File.WriteAllBytesAsync(concatenated, [.. frame, .. frame]);
        // image2 guesses a single image from the extension, but the file actually contains two frames.
        Assert.Equal("image2", await ProbeFormatAsync(concatenated));

        await AssertRejectedImportAsync(concatenated);
    }

    private async Task AssertRejectedImportAsync(string source)
    {
        var service = new WorkspaceService(new JsonProjectRepository(), new FfprobeMediaReader(_tools));
        var session = await service.CreateAsync(Path.Combine(_root, "workspace"), "静态图片验证");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportMediaAsync(session, source, MediaKind.Image));
        Assert.Empty(session.Project.MediaAssets);
        Assert.Empty(session.Project.VideoClips);
        Assert.Empty(Directory.GetFiles(Path.Combine(session.RootPath, "media", "images")));
        Assert.True(File.Exists(source));
    }

    private async Task<string> CreateJpegWithLargeExifHeaderAsync(string extension)
    {
        Directory.CreateDirectory(_root);
        string baseline = Path.Combine(_root, "baseline.jpg");
        await MediaProcess.RunAsync(_tools.FfmpegPath,
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=96x64",
                "-frames:v", "1", "-update", "1", baseline]);
        byte[] original = await File.ReadAllBytesAsync(baseline);
        Assert.Equal(new byte[] { 0xff, 0xd8 }, original[..2]);

        // A valid empty little-endian EXIF directory with padding reproduces photographs whose
        // initial metadata hides JPEG frame markers from ffprobe's format auto-detection.
        const int payloadLength = 60000;
        byte[] metadata = new byte[4 + payloadLength];
        metadata[0] = 0xff;
        metadata[1] = 0xe1; // APP1
        metadata[2] = (byte)((payloadLength + 2) >> 8);
        metadata[3] = (byte)((payloadLength + 2) & 0xff);
        new byte[] { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'I', (byte)'I', 0x2a, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0 }.CopyTo(metadata, 4);
        string source = Path.Combine(_root, $"保存的图片 带 EXIF.{extension}");
        await File.WriteAllBytesAsync(source, [.. original[..2], .. metadata, .. original[2..]]);
        return source;
    }

    private async Task<string?> ProbeFormatAsync(string source)
    {
        string json = await MediaProcess.RunAsync(_tools.FfprobePath,
            ["-v", "error", "-show_entries", "format=format_name", "-of", "json", source]);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("format").GetProperty("format_name").GetString();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
