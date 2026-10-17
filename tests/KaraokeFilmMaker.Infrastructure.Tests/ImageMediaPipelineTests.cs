using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Timeline;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;
using KaraokeFilmMaker.Infrastructure.Audio;
using KaraokeFilmMaker.Infrastructure.Export;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Infrastructure.Workspaces;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class ImageMediaPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "KaraokeFilmMaker.ImageTests", Guid.NewGuid().ToString("N"));
    private readonly MediaToolchain _tools = MediaToolchain.Find();

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("jpeg")]
    [InlineData("bmp")]
    public async Task ImageImportCopiesOriginalAndCreatesFiveSecondSilentClipWithReusablePng(string extension)
    {
        string source = await CreateImageAsync(extension);
        var service = new WorkspaceService(new JsonProjectRepository(), new FfprobeMediaReader(_tools));
        var session = await service.CreateAsync(Path.Combine(_root, "workspace"), "图片 项目");
        session = await service.ImportMediaAsync(session, source, MediaKind.Image);
        var asset = Assert.Single(session.Project.MediaAssets);
        Assert.Equal(MediaKind.Image, asset.Kind);
        Assert.Equal(ProjectDuration.Zero, asset.Duration);
        Assert.Null(session.Project.MasterAudioAssetId);
        Assert.True(File.Exists(Path.Combine(session.RootPath, asset.RelativePath)));
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(Path.Combine(session.RootPath, asset.RelativePath)));
        Assert.Equal(64, asset.ContentHash!.Length);
        var clip = Assert.Single(session.Project.VideoClips);
        Assert.True(clip.IsMuted);
        Assert.Equal(5, clip.Duration.TotalSeconds);
        Assert.Equal(ProjectTime.Zero, clip.SourceStart);
        var cache = new ImagePreviewCache(_tools);
        var files = await cache.PrepareAsync(session.RootPath, asset);
        Assert.Equal(".png", Path.GetExtension(files.ProxyPath));
        Assert.True(File.Exists(files.ThumbnailPath));
        Assert.Equal(files, await cache.PrepareAsync(session.RootPath, asset));
        var details = await new FfprobeMediaReader(_tools).ReadDetailsAsync(files.ProxyPath);
        Assert.Equal(90, details.Width);
        Assert.Equal(180, details.Height);
        Assert.False(details.HasAudio);
    }

    [Fact]
    public async Task StillImageCannotBeImportedAsZeroDurationVideoOrAudio()
    {
        var source = await CreateImageAsync("png");
        var service = new WorkspaceService(new JsonProjectRepository(), new FfprobeMediaReader(_tools));
        var session = await service.CreateAsync(Path.Combine(_root, "workspace"), "Valid imports only");
        foreach (MediaKind kind in new[] { MediaKind.Video, MediaKind.Audio })
        {
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.ImportMediaAsync(session, source, kind));
            Assert.Contains("导入图片", error.Message);
        }
        Assert.Empty(session.Project.MediaAssets);
        Assert.Empty(session.Project.VideoClips);
        Assert.Empty(Directory.GetFiles(Path.Combine(session.RootPath, "media", "video")));
        Assert.Empty(Directory.GetFiles(Path.Combine(session.RootPath, "media", "audio")));
    }

    [Fact]
    public async Task OversizedImageImportReportsLimitsAndRemovesItsCopy()
    {
        Directory.CreateDirectory(_root);
        string source = Path.Combine(_root, "too-wide.png");
        await MediaProcess.RunAsync(_tools.FfmpegPath, ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i",
            "color=c=red:s=20000x2", "-frames:v", "1", "-update", "1", source]);
        var service = new WorkspaceService(new JsonProjectRepository(), new FfprobeMediaReader(_tools));
        var session = await service.CreateAsync(Path.Combine(_root, "workspace"), "图片尺寸限制");
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ImportMediaAsync(session, source, MediaKind.Image));
        Assert.Contains("16,384", error.Message);
        Assert.Contains("先缩小", error.Message);
        Assert.Empty(session.Project.MediaAssets);
        Assert.Empty(Directory.GetFiles(Path.Combine(session.RootPath, "media", "images")));
        Assert.True(File.Exists(source));
    }

    [Theory]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(2160, 3840, 1080, 1920)]
    [InlineData(3840, 3840, 1920, 1920)]
    public async Task LargeImageCachePreservesDetailInBothOrientationsWithoutAlteringTheOriginal(
        int width, int height, int expectedWidth, int expectedHeight)
    {
        Directory.CreateDirectory(_root);
        string source = Path.Combine(_root, "4k.png");
        await MediaProcess.RunAsync(_tools.FfmpegPath, ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i",
            $"color=c=red:s={width}x{height}", "-frames:v", "1", "-update", "1", source]);
        var service = new WorkspaceService(new JsonProjectRepository(), new FfprobeMediaReader(_tools));
        var session = await service.CreateAsync(Path.Combine(_root, "workspace"), "4K image");
        session = await service.ImportMediaAsync(session, source, MediaKind.Image);
        var asset = Assert.Single(session.Project.MediaAssets);
        var files = await new ImagePreviewCache(_tools).PrepareAsync(session.RootPath, asset);
        var reader = new FfprobeMediaReader(_tools);
        var cached = await reader.ReadDetailsAsync(files.ProxyPath);
        var imported = await reader.ReadDetailsAsync(Path.Combine(session.RootPath, asset.RelativePath));
        Assert.Equal(expectedWidth, cached.Width);
        Assert.Equal(expectedHeight, cached.Height);
        Assert.Equal(width, imported.Width);
        Assert.Equal(height, imported.Height);
    }

    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(8000, 6000)]
    [InlineData(12000, 9000)]
    [InlineData(16384, 100)]
    [InlineData(12000, 10000)]
    public void OrdinaryPhonePhotosAndBoundaryDimensionsAreSupported(int width, int height)
        => ImageMediaLimits.ValidateDimensions(width, height);

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(16385, 10)]
    [InlineData(10, 16385)]
    [InlineData(12000, 10001)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void InvalidOrOversizedImageDimensionsHaveFriendlyErrors(int width, int height)
        => Assert.Throws<InvalidDataException>(() => ImageMediaLimits.ValidateDimensions(width, height));

    [Fact]
    public async Task ImageAppendsToVideoAndCanRoundTripAsLongStillWithoutItsOriginalFile()
    {
        var session = await CreateSongProjectAsync();
        var service = new WorkspaceService(new JsonProjectRepository(), new FfprobeMediaReader(_tools));
        string source = await CreateImageAsync("png");
        session = await service.ImportMediaAsync(session, source, MediaKind.Image);
        var first = session.Project.VideoClips[0];
        var second = session.Project.VideoClips[1];
        Assert.Equal(first.TimelineEnd, second.TimelineStart);
        var timeline = new VideoTimelineService();
        session = session with { Project = timeline.UpdateClip(session.Project, second.Id, second.TimelineStart,
            ProjectTime.Zero, ProjectDuration.FromSeconds(60), true) };
        await service.SaveAsync(session);
        File.Delete(source);
        var reopened = await service.OpenAsync(session.ProjectFilePath!);
        Assert.Equal(60, reopened.Project.VideoClips[1].Duration.TotalSeconds);
        Assert.Equal(session.Project.MasterAudioAssetId, reopened.Project.MasterAudioAssetId);
        Assert.Equal(ProjectTime.Zero, reopened.Project.VideoClips[1].SourceStart);
        var image = reopened.Project.MediaAssets.Single(asset => asset.Kind == MediaKind.Image);
        Assert.True(File.Exists((await new ImagePreviewCache(_tools).PrepareAsync(reopened.RootPath, image)).ProxyPath));
    }

    [Fact]
    public async Task ImageVideoAndBlackGapsExportWithAspectRatioAndSongLength()
    {
        var session = await CreateSongProjectAsync();
        var service = new WorkspaceService(new JsonProjectRepository(), new FfprobeMediaReader(_tools));
        session = await service.ImportMediaAsync(session, await CreateImageAsync("png"), MediaKind.Image);
        var image = session.Project.MediaAssets.Single(asset => asset.Kind == MediaKind.Image);
        var video = session.Project.MediaAssets.Single(asset => asset.Kind == MediaKind.Video);
        var project = session.Project with
        {
            ExportPreset = session.Project.ExportPreset with { Width = 320, Height = 180, FramesPerSecond = 30 },
            VideoClips = [
                new VideoClip(Guid.NewGuid(), image.Id, ProjectTime.FromSeconds(0.25), ProjectTime.Zero, ProjectDuration.FromSeconds(0.5), true),
                new VideoClip(Guid.NewGuid(), video.Id, ProjectTime.FromSeconds(0.75), ProjectTime.Zero, ProjectDuration.FromSeconds(0.5), true),
                new VideoClip(Guid.NewGuid(), image.Id, ProjectTime.FromSeconds(1.5), ProjectTime.Zero, ProjectDuration.FromSeconds(5), true)],
        };
        string output = Path.Combine(session.RootPath, "exports", "still-and-video.mp4");
        await new VideoExportService(_tools).ExportAsync(session.RootPath, project, output);
        var details = await new FfprobeMediaReader(_tools).ReadDetailsAsync(output);
        Assert.Equal(320, details.Width);
        Assert.Equal(180, details.Height);
        Assert.Equal(30, details.FrameRate);
        Assert.True(details.HasAudio);
        Assert.InRange(details.Duration.TotalSeconds, 2.35, 2.5);
        AssertBlack(await PixelAsync(output, 0.1, 160));
        AssertRed(await PixelAsync(output, 0.4, 160));
        AssertRed(await PixelAsync(output, 0.4, 10)); // Portrait images cover the frame with centered cropping.
        byte[] blue = await PixelAsync(output, 0.9, 160);
        Assert.True(blue[0] > 150 && blue[2] < 60);
        AssertBlack(await PixelAsync(output, 1.35, 160));
        AssertRed(await PixelAsync(output, 2.2, 160)); // Still continues well beyond its one decoded frame.
    }

    [Fact]
    public async Task ImagesNeverEnterAudioMixEvenWhenTheirMuteFlagIsOff()
    {
        var session = await CreateSongProjectAsync();
        var service = new WorkspaceService(new JsonProjectRepository(), new FfprobeMediaReader(_tools));
        session = await service.ImportMediaAsync(session, await CreateImageAsync("png"), MediaKind.Image);
        var composer = new ProjectAudioComposer(_tools);
        string original = await composer.PrepareAsync(session.RootPath, session.Project);
        var image = session.Project.MediaAssets.Single(asset => asset.Kind == MediaKind.Image);
        var altered = session.Project with { VideoClips = session.Project.VideoClips.Select(clip =>
            clip.MediaAssetId == image.Id ? clip with { TimelineStart = ProjectTime.Zero, IsMuted = false } : clip).ToArray() };
        // Missing image still cannot be probed as an audio source or invalidate the audio cache.
        File.Delete(Path.Combine(session.RootPath, image.RelativePath));
        Assert.Equal(original, await composer.PrepareAsync(session.RootPath, altered));
    }

    [Fact]
    public async Task CorruptOrRenamedVideoImageImportsLeaveNoPartialAssetOrClip()
    {
        var session = await CreateSongProjectAsync();
        var service = new WorkspaceService(new JsonProjectRepository(), new FfprobeMediaReader(_tools));
        string bad = Path.Combine(_root, "fake.png");
        await File.WriteAllBytesAsync(bad, [1, 2, 3]);
        await Assert.ThrowsAnyAsync<Exception>(() => service.ImportMediaAsync(session, bad, MediaKind.Image));
        var video = session.Project.MediaAssets.Single(asset => asset.Kind == MediaKind.Video);
        File.Copy(Path.Combine(session.RootPath, video.RelativePath), bad, true);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportMediaAsync(session, bad, MediaKind.Image));
        Assert.Empty(Directory.GetFiles(Path.Combine(session.RootPath, "media", "images")));
        Assert.DoesNotContain(session.Project.MediaAssets, asset => asset.Kind == MediaKind.Image);
    }

    private async Task<WorkspaceSession> CreateSongProjectAsync()
    {
        Directory.CreateDirectory(_root);
        string song = Path.Combine(_root, "song.wav");
        string video = Path.Combine(_root, "blue.mp4");
        await MediaProcess.RunAsync(_tools.FfmpegPath, ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i",
            "sine=frequency=440:duration=2.4", "-ar", "48000", "-ac", "2", song]);
        await MediaProcess.RunAsync(_tools.FfmpegPath, ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i",
            "color=c=blue:s=320x180:r=30:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", video]);
        var service = new WorkspaceService(new JsonProjectRepository(), new FfprobeMediaReader(_tools));
        var session = await service.CreateAsync(Path.Combine(_root, "workspace"), "图片与视频");
        session = await service.ImportMediaAsync(session, song, MediaKind.Audio);
        return await service.ImportMediaAsync(session, video, MediaKind.Video);
    }

    private async Task<string> CreateImageAsync(string extension)
    {
        Directory.CreateDirectory(_root);
        string image = Path.Combine(_root, $"竖向 图片.{extension}");
        await MediaProcess.RunAsync(_tools.FfmpegPath, ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i",
            "color=c=red:s=90x180", "-frames:v", "1", "-update", "1", image]);
        return image;
    }

    private async Task<byte[]> PixelAsync(string video, double seconds, int x)
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".png");
        await MediaProcess.RunAsync(_tools.FfmpegPath, ["-nostdin", "-v", "error", "-y", "-ss", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", video, "-frames:v", "1", "-update", "1", path]);
        using FileStream stream = File.OpenRead(path);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new System.Windows.Int32Rect(x, 50, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static void AssertBlack(byte[] pixel) => Assert.True(pixel[0] < 20 && pixel[1] < 20 && pixel[2] < 20);
    private static void AssertRed(byte[] pixel) => Assert.True(pixel[2] > 150 && pixel[0] < 60 && pixel[1] < 60);

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
