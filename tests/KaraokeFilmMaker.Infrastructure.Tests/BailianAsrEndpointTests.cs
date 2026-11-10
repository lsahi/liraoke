using System.Net;
using System.Net.Http;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Application.Preferences;
using KaraokeFilmMaker.Infrastructure.Ai;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class BailianAsrEndpointTests
{
    [Theory]
    [InlineData("https://ws-test.cn-beijing.maas.aliyuncs.com/compatible-mode/v1", "qwen-chat", "https://ws-test.cn-beijing.maas.aliyuncs.com/compatible-mode/v1/chat/completions")]
    [InlineData(" https://ws-test.cn-beijing.maas.aliyuncs.com/compatible-mode/v1/ ", "qwen-chat", "https://ws-test.cn-beijing.maas.aliyuncs.com/compatible-mode/v1/chat/completions")]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-chat", "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions")]
    [InlineData("https://ws-test.cn-beijing.maas.aliyuncs.com/compatible-mode/v1/chat/completions", "qwen-chat", "https://ws-test.cn-beijing.maas.aliyuncs.com/compatible-mode/v1/chat/completions")]
    [InlineData("https://relay.example/custom-asr", "qwen-chat", "https://relay.example/custom-asr")]
    [InlineData("http://localhost:8000/v1/", "transcriptions", "http://localhost:8000/v1/audio/transcriptions")]
    public async Task RequestUsesCompleteEndpoint(string input, string protocol, string expected)
    {
        using var client = new HttpClient(new CaptureHandler(request => {
            Assert.Equal(expected, request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-key", request.Headers.Authorization!.Parameter);
            return new(HttpStatusCode.OK) { Content = new StringContent(protocol == "transcriptions"
                ? "{\"text\":\"なさ\"}" : "data: {\"choices\":[{\"delta\":{\"content\":\"なさ\"}}]}\n\ndata: [DONE]\n\n") };
        }));
        var service = new QwenReadingCorrectionService("unused", client);
        var result = new List<string>();
        await foreach (var item in service.TranscribeAsync([1], new(Endpoint: input, Protocol: protocol), new("test-key"), default)) result.Add(item);
        Assert.Equal("なさ", string.Concat(result));
    }

    [Theory]
    [InlineData("https://ws-test.cn-beijing.maas.aliyuncs.com/api/v1")]
    [InlineData("https://ws-test.cn-beijing.maas.aliyuncs.com")]
    [InlineData("https://dashscope.aliyuncs.com/api/v1/services/audio/asr/transcription")]
    public void NativeDashScopeAddressExplainsWhichAddressToCopy(string endpoint)
    {
        var error = Assert.Throws<InvalidOperationException>(() => QwenReadingCorrectionService.ValidateSettings(new(Endpoint: endpoint), new("test-key")));
        Assert.Contains("OpenAI 兼容地址", error.Message);
    }

    [Theory]
    [InlineData("https://example.com/v1?api_key=secret")]
    [InlineData("https://user:secret@example.com/v1")]
    [InlineData("https://example.com/v1#secret")]
    public void NormalizationDoesNotStripInvalidCredentialsAndSendRequest(string endpoint)
    {
        var error = Assert.Throws<InvalidOperationException>(() => QwenReadingCorrectionService.ResolveEndpoint(new(Endpoint: endpoint)));
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public void SavedBaseUrlReloadsAsCompleteEndpointWithSameCredentials()
    {
        string root = Path.Combine(Path.GetTempPath(), "BailianEndpoint-" + Guid.NewGuid());
        try {
            var store = new UserPreferenceStore(root);
            store.SaveAsr(new(Endpoint: "https://ws-test.cn-beijing.maas.aliyuncs.com/compatible-mode/v1"), new("test-only-key"));
            var settings = store.Load().Asr!;
            Assert.Equal("https://ws-test.cn-beijing.maas.aliyuncs.com/compatible-mode/v1/chat/completions", settings.Endpoint);
            Assert.Equal("test-only-key", UserPreferenceStore.ReadAsrCredentials(settings).APIKey);
            Assert.DoesNotContain("test-only-key", File.ReadAllText(store.FilePath));
        } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(handle(request));
    }
}
