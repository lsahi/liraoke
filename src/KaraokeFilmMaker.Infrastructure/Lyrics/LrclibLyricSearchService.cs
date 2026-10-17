using System.Net;
using System.Net.Http;
using System.Text.Json;
using KaraokeFilmMaker.Application.Abstractions;

namespace KaraokeFilmMaker.Infrastructure.Lyrics;

/// <summary>Explicit, read-only requests; no audio uploads, scraping, automatic retries or AI calls.</summary>
public sealed class LrclibLyricSearchService(HttpClient client, string? projectContact = null) : ILyricSearchService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<OnlineLyricResult> Results)> _cache = [];
    private DateTimeOffset _nextRequest;
    private DateTimeOffset _rateLimitUntil;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<OnlineLyricResult>> SearchAsync(LyricSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Title);
        string title = query.Title.Trim(), artist = query.Artist?.Trim() ?? "";
        if (title.Length is < 1 or > 200 || artist.Length > 200)
        { throw new ArgumentException("歌名不能为空，歌名和歌手均不能超过 200 个字符。"); }
        string term = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_cache.TryGetValue(term, out var cached) && now - cached.At < TimeSpan.FromMinutes(5)) { return cached.Results; }
            if (_rateLimitUntil > now)
            { throw new HttpRequestException($"歌词服务限流，请在 {Math.Ceiling((_rateLimitUntil - now).TotalSeconds)} 秒后重试。"); }
            if (_nextRequest > now) { await Task.Delay(_nextRequest - now, cancellationToken).ConfigureAwait(false); }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://lrclib.net/api/search?q=" + Uri.EscapeDataString(term));
            request.Headers.UserAgent.ParseAdd("KaraokeFilmMaker/0.3.0");
            if (!string.IsNullOrWhiteSpace(projectContact))
            { request.Headers.UserAgent.ParseAdd("(" + projectContact.Replace("(", "").Replace(")", "") + ")"); }
            request.Headers.Accept.ParseAdd("application/json");
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    TimeSpan wait = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(60);
                    wait = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
                    _rateLimitUntil = DateTimeOffset.UtcNow + wait;
                    throw new HttpRequestException($"歌词服务限流，请在 {Math.Ceiling(wait.TotalSeconds)} 秒后重试。", null, response.StatusCode);
                }
                if (!response.IsSuccessStatusCode)
                { throw new HttpRequestException($"歌词服务返回 HTTP {(int)response.StatusCode}；请稍后重试，也可粘贴已授权歌词或从文件导入。", null, response.StatusCode); }
                const int maximumBytes = 2 * 1024 * 1024;
                if (response.Content.Headers.ContentLength > maximumBytes) { throw new InvalidDataException("歌词服务响应过大。"); }
                await using Stream stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                byte[] block = new byte[16 * 1024];
                int count;
                while ((count = await stream.ReadAsync(block, deadline.Token).ConfigureAwait(false)) != 0)
                {
                    if (buffer.Length + count > maximumBytes) { throw new InvalidDataException("歌词服务响应过大。"); }
                    await buffer.WriteAsync(block.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                }
                var records = JsonSerializer.Deserialize<ApiRecord?[]>(buffer.ToArray(), JsonOptions)
                    ?? throw new InvalidDataException("歌词服务未返回有效的结果列表。");
                var results = records.OfType<ApiRecord>().Where(item => item.Id > 0 && !string.IsNullOrWhiteSpace(item.TrackName)
                    && double.IsFinite(item.Duration) && item.Duration is >= 0 and <= 604800
                    && (item.PlainLyrics?.Length ?? 0) <= 256_000 && (item.SyncedLyrics?.Length ?? 0) <= 256_000)
                    .DistinctBy(item => item.Id).Take(30)
                    .Select(item => new OnlineLyricResult(item.Id, item.TrackName!, item.ArtistName ?? "", item.AlbumName ?? "", item.Duration,
                        item.Instrumental, item.PlainLyrics, item.SyncedLyrics, "LRCLIB", $"https://lrclib.net/api/get/{item.Id}"))
                    .ToArray();
                if (_cache.Count >= 5) { _cache.Clear(); }
                _cache[term] = (DateTimeOffset.UtcNow, results);
                return results;
            }
            catch (JsonException exception) { throw new InvalidDataException("歌词服务响应格式发生变化；请稍后重试。", exception); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("歌词搜索超时，请检查网络或改用浏览器搜索。"); }
            finally { _nextRequest = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(300); }
        }
        finally { _gate.Release(); }
    }

    private sealed record ApiRecord(long Id, string? TrackName, string? ArtistName, string? AlbumName, double Duration,
        bool Instrumental, string? PlainLyrics, string? SyncedLyrics);
}
