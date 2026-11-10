using System.Net;
using System.Net.Http;
using System.Text;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Infrastructure.Ai;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class AsrDiagnosticTests
{
    [Theory]
    [InlineData("https://ws-test.cn-beijing.maas.aliyuncs.com/compatible-mode/v1", "transcriptions")]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", "transcriptions")]
    [InlineData("http://localhost:8000/v1/audio/transcriptions", "qwen-chat")]
    public void ProtocolMismatchIsRejectedBeforeNetwork(string endpoint, string protocol)
    {
        Assert.Throws<InvalidOperationException>(() => QwenReadingCorrectionService.ValidateSettings(new(Endpoint: endpoint, Protocol: protocol), new("test")));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LogsStatusAndStageWithoutSecretsOrPayloads(bool networkFailure)
    {
        string root = Path.Combine(Path.GetTempPath(), "AsrLog-" + Guid.NewGuid());
        try {
            var log = new AsrDiagnosticLog(root);
            using var client = new HttpClient(log.Wrap(new Handler(request => {
                if (networkFailure) throw new HttpRequestException("secret-network-url-and-key");
                return new(HttpStatusCode.Unauthorized) { Content = new StringContent("secret-server-body") };
            })));
            var service = new QwenReadingCorrectionService("unused", client, log);
            try {
                await foreach (var _ in service.TranscribeAsync(Encoding.UTF8.GetBytes("secret-audio"),
                    new(Endpoint: "https://example.invalid/secret-path"), new("secret-api-key"), default)) { }
                Assert.Fail("Expected request failure");
            } catch (Exception e) when (e is HttpRequestException or ReadingServiceHttpException) { log.Failure("asr", e); }
            string data = File.ReadAllText(log.FilePath);
            Assert.Contains("http", data); Assert.Contains("asr", data);
            if (!networkFailure) Assert.Contains("401", data);
            Assert.DoesNotContain("secret", data);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("secret-audio")), data);
            Assert.DoesNotContain("example.invalid", data);
        } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void LogWriteFailureDoesNotBreakWorkflow()
    {
        string path = Path.GetTempFileName();
        try {
            var log = new AsrDiagnosticLog(path);
            Assert.False(log.IsAvailable);
            log.Failure("asr", new IOException("secret"));
        } finally { File.Delete(path); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }
}
