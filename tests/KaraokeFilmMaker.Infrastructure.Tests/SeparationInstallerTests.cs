using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using KaraokeFilmMaker.Infrastructure.Separation;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class SeparationInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Liraoke-SeparationTests-" + Guid.NewGuid().ToString("N"));
    private static readonly Uri Url = new("https://download.pytorch.org/whl/test.whl");
    private string FilePath(string name) { Directory.CreateDirectory(_root); return Path.Combine(_root, name); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    [Fact]
    public void ProductionManifestIsFixedAndOnlyAllowsOfficialHttps()
    {
        var manifest = GpuRuntimeManifest.Load();
        Assert.Equal(3461384651, manifest.WheelBytes);
        Assert.Equal(64, manifest.WheelSha256.Length);
        Assert.True(GpuRuntimeManifest.IsOfficialUrl(new(manifest.WheelUrl)));
        Assert.False(GpuRuntimeManifest.IsOfficialUrl(new("http://download.pytorch.org/file")));
        Assert.False(GpuRuntimeManifest.IsOfficialUrl(new("https://download.pytorch.org.evil.test/file")));
        Assert.False(GpuRuntimeManifest.IsOfficialUrl(new("https://user@download.pytorch.org/file")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResumesOrSafelyRestartsWhenServerIgnoresRange(bool supportsRange)
    {
        byte[] payload = Encoding.UTF8.GetBytes("official verified payload");
        string file = FilePath("runtime.whl");
        await File.WriteAllBytesAsync(file + ".partial", payload[..5]);
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(5, request.Headers.Range!.Ranges.Single().From);
            var response = new HttpResponseMessage(supportsRange ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { Content = new ByteArrayContent(supportsRange ? payload[5..] : payload) };
            if (supportsRange) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(5, payload.Length - 1, payload.Length);
            return response;
        }));
        await new VerifiedComponentDownload(client).DownloadAsync(Url, payload.Length, Hash(payload), file, null, default);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file));
        Assert.False(File.Exists(file + ".partial"));
    }

    [Fact]
    public async Task HashFailureNeverPromotesAnArchive()
    {
        byte[] payload = [1, 2, 3]; string file = FilePath("runtime.whl");
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new VerifiedComponentDownload(client)
            .DownloadAsync(Url, 3, Hash([3, 2, 1]), file, null, default));
        Assert.False(File.Exists(file)); Assert.False(File.Exists(file + ".partial"));
    }

    [Fact]
    public async Task MismatchedContentRangeIsRejectedAndExistingBytesPreserved()
    {
        string file = FilePath("runtime.whl"); await File.WriteAllBytesAsync(file + ".partial", [1]);
        using var client = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([2, 3]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 1, 3); return response;
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new VerifiedComponentDownload(client)
            .DownloadAsync(Url, 3, Hash([1, 2, 3]), file, null, default));
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(file + ".partial"));
    }

    [Fact]
    public async Task CancelledDownloadDoesNotTouchValidInstallationOrRequestNetwork()
    {
        string file = FilePath("runtime.whl"); await File.WriteAllBytesAsync(file + ".partial", [1]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var client = new HttpClient(new Handler(_ => throw new Exception("must not request")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VerifiedComponentDownload(client)
            .DownloadAsync(Url, 3, Hash([1, 2, 3]), file, null, cancellation.Token));
        Assert.True(File.Exists(file + ".partial")); Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("torch/../../escape.dll")]
    [InlineData("C:/escape.dll")]
    public async Task ArchiveCannotEscapePrivateInstallation(string name)
    {
        string zip = FilePath("bad.whl");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("invalid");
        await Assert.ThrowsAsync<InvalidDataException>(() => SafeRuntimeArchive.ExtractWheelAsync(zip, FilePath("staging"), default));
        Assert.False(File.Exists(FilePath("escape.dll")));
    }

    [Fact]
    public async Task RuntimeExtractionPreservesDllsAndLicensesButOmitsBuildInputs()
    {
        string zip = FilePath("good.whl");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (string name in new[] { "torch/lib/torch_cuda.dll", "torch/lib/dnnl.lib", "torch/include/torch.h", "torch-test.dist-info/licenses/LICENSE" })
            { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write("content"); }
        }
        string output = FilePath("staging");
        await SafeRuntimeArchive.ExtractWheelAsync(zip, output, default);
        Assert.True(File.Exists(Path.Combine(output, "torch/lib/torch_cuda.dll")));
        Assert.True(File.Exists(Path.Combine(output, "torch-test.dist-info/licenses/LICENSE")));
        Assert.False(File.Exists(Path.Combine(output, "torch/lib/dnnl.lib")));
        Assert.False(File.Exists(Path.Combine(output, "torch/include/torch.h")));
    }

    [Fact]
    public void UntrustedActivePointerCannotSelectOrDeleteOutsideComponentRoot()
    {
        string components = FilePath("components"); Directory.CreateDirectory(components);
        File.WriteAllText(Path.Combine(components, "active.json"), "\"../outside\"");
        string outside = FilePath("outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "user data");
        var runtime = new DemucsRuntimeManager(FilePath("bundle"), components);
        Assert.Null(runtime.InstalledGpuPath);
        runtime.UninstallGpu(); Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void OperationsAreExclusiveAcrossInstances()
    {
        var runtime = new DemucsRuntimeManager(FilePath("bundle"), FilePath("components"));
        using var first = runtime.AcquireOperationLock();
        Assert.Throws<IOException>(() => runtime.AcquireOperationLock());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(action(request));
    }
}
