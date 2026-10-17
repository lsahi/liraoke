using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Infrastructure.Lyrics;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class LyricSearchTests
{
    private const string Response = """
        [{"id":17,"trackName":"テスト曲","artistName":"自作歌手","albumName":"demo","duration":123.5,
        "instrumental":false,"plainLyrics":"自作の歌\n未来へ","syncedLyrics":"[00:01.00]自作の歌\n[00:03.00]未来へ"},
        {"id":18,"trackName":"伴奏","artistName":"demo","duration":123.5,"instrumental":true,
        "plainLyrics":null,"syncedLyrics":null}]
        """;

    [Fact]
    public async Task UnicodeQueryUsesPublicApiClientIdentityAndCachesResults()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Response) });
        using var client = new HttpClient(handler);
        var service = new LrclibLyricSearchService(client, "https://example.test/project");
        var result = await service.SearchAsync(new LyricSearchQuery(" テスト曲 ", "自作歌手"));
        Assert.Equal(2, result.Count); Assert.True(result[0].HasTiming); Assert.True(result[0].CanImport);
        Assert.False(result[1].CanImport);
        Assert.Equal("https://lrclib.net", handler.Uri!.GetLeftPart(UriPartial.Authority));
        Assert.Contains("テスト曲 自作歌手", Uri.UnescapeDataString(handler.Uri.Query));
        Assert.Contains("KaraokeFilmMaker/0.3.0", handler.UserAgent);
        Assert.Same(result, await service.SearchAsync(new LyricSearchQuery("テスト曲", "自作歌手")));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task RateLimitDoesNotRetryAndHonorsRetryAfterOnNextSearch()
    {
        using var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120)); return response;
        });
        using var client = new HttpClient(handler);
        var service = new LrclibLyricSearchService(client);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync(new("曲")));
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync(new("别的曲")));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task MalformedResponseIsARecoverableError()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>unavailable</html>") });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LrclibLyricSearchService(client).SearchAsync(new("曲")));
    }

    [Fact]
    public async Task CancellationDoesNotSendRequestAndEmptyQueryIsRejected()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        using var client = new HttpClient(handler);
        var service = new LrclibLyricSearchService(client);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync(new("曲"), cancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new(" ")));
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task OversizedResponseAndTimeoutAreReportedWithoutImportingAnything()
    {
        using var handler = new StubHandler(_ =>
        {
            var content = new StringContent("[]"); content.Headers.ContentLength = 3 * 1024 * 1024;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LrclibLyricSearchService(client).SearchAsync(new("曲")));
        using var timeoutHandler = new StubHandler(_ => throw new OperationCanceledException());
        using var timeoutClient = new HttpClient(timeoutHandler);
        await Assert.ThrowsAsync<TimeoutException>(() => new LrclibLyricSearchService(timeoutClient).SearchAsync(new("曲")));
    }

    [Theory]
    [InlineData(true, "LRCLIB", "https://lrclib.net/api/get/17")]
    [InlineData(false, "LRCLIB", "https://lrclib.net/api/get/17")]
    [InlineData(false, "User supplied", "https://utaten.com/lyric/legacy-source")]
    public async Task ImportPreservesOriginalTextAndPortableSource(bool lrc, string provider, string sourceUrl)
    {
        string root = Path.Combine(Path.GetTempPath(), "KaraokeFilmMaker.SearchTests", Guid.NewGuid().ToString("N"));
        try
        {
            string text = lrc ? "[00:01.00]自作の歌\n[00:03.00]未来へ" : "自作の歌\n未来へ";
            var document = await new OnlineLyricImporter().ImportAsync(root,
                new(text, lrc, provider, "测试歌", "demo", sourceUrl), LyricLanguage.Japanese);
            Assert.Equal(2, document.Lines.Count); Assert.Equal(lrc ? 1 : 0, document.Lines[0].Timing.Start.TotalSeconds);
            Assert.NotNull(document.Source);
            string file = Path.Combine(root, document.Source.ImportedFileRelativePath);
            Assert.Equal(text, await File.ReadAllTextAsync(file));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "lyrics"), "*.source.json"));
            var repository = new JsonProjectRepository();
            await repository.SaveAsync(root, KaraokeProject.Create("联网歌词", DateTimeOffset.UtcNow) with { Lyrics = document });
            Assert.Equal(document.Source, (await repository.LoadAsync(root)).Lyrics.Source);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Fact]
    public async Task InvalidTextAndUnsafeSourcesLeaveNoFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "KaraokeFilmMaker.SearchTests", Guid.NewGuid().ToString("N"));
        var importer = new OnlineLyricImporter();
        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(root,
            new("text", true, "Manual", "", "", null), LyricLanguage.Japanese));
        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(root,
            new("text", false, "Manual", "", "", "file:///C:/windows/win.ini"), LyricLanguage.Japanese));
        Assert.False(Directory.Exists(root));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Count { get; private set; }
        public Uri? Uri { get; private set; }
        public string UserAgent { get; private set; } = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++; Uri = request.RequestUri; UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(response(request));
        }
    }
}
