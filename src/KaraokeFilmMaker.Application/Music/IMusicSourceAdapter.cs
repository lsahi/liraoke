namespace KaraokeFilmMaker.Application.Music;

[Flags]
public enum MusicSourceCapabilities { None = 0, Search = 1, Preview = 2, ImportAudio = 4, Account = 8 }
public enum MediaAuthenticationKind { None, Cookie, OAuthPkce, ApiKey }
public sealed record MusicSourceDescriptor(string Id, string Name, MusicSourceCapabilities Capabilities,
    MediaAuthenticationKind Authentication, string? Help = null);
public sealed record MusicTrack(string SourceId, string Id, string Title, string Artist, string Album,
    double? DurationSeconds, string? CoverUrl = null)
{
    public string DurationLabel => DurationSeconds is > 0 ? TimeSpan.FromSeconds(DurationSeconds.Value).ToString(@"m\:ss") : "未知";
    public string SourceLabel => SourceId switch { "netease" => "网易云", "tencent" => "QQ 音乐", "qishui" => "汽水", _ => SourceId };
}
// Transient connection secrets and expiring URLs are not project data or Agent context.
public sealed record MusicSourceCredentials(string Cookie = "", string? SignerEndpoint = null);
public sealed record ResolvedMusicAudio(Uri Url, string Quality, bool PreviewOnly = false, string? DecodeKey = null);
public interface IMusicSourceAdapter
{
    MusicSourceDescriptor Descriptor { get; }
    Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, MusicSourceCredentials credentials, CancellationToken cancellationToken = default);
    Task<ResolvedMusicAudio> ResolveAsync(MusicTrack track, MusicSourceCredentials credentials, CancellationToken cancellationToken = default);
}

public sealed class MusicSourceRegistry(IEnumerable<IMusicSourceAdapter> adapters)
{
    public IReadOnlyList<IMusicSourceAdapter> Sources { get; } = adapters.ToArray();
    public IMusicSourceAdapter Get(string id) => Sources.Single(source => source.Descriptor.Id == id);
    public async Task<MusicSearchResult> SearchAsync(string query, string? sourceId,
        Func<string, MusicSourceCredentials> credentials, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length > 200) throw new ArgumentException("请输入 1–200 字的歌名或歌手。");
        var sources = sourceId is null ? Sources : [Get(sourceId)];
        var results = await Task.WhenAll(sources.Where(source => source.Descriptor.Capabilities.HasFlag(MusicSourceCapabilities.Search)).Select(async source =>
        {
            try { return new MusicSearchResult(await source.SearchAsync(query.Trim(), credentials(source.Descriptor.Id), cancellationToken), []); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is InvalidOperationException or IOException or System.Net.Http.HttpRequestException or OperationCanceledException)
            { return new MusicSearchResult([], [$"{source.Descriptor.Name}：搜索暂不可用，请重试或检查账号设置。"]); }
        }));
        // Preserve original titles, versions and provider IDs; never silently substitute a cover/remix.
        return new MusicSearchResult(results.SelectMany(x => x.Tracks).DistinctBy(x => (x.SourceId, x.Id)).ToArray(),
            results.SelectMany(x => x.Warnings).ToArray());
    }
}
public sealed record MusicSearchResult(IReadOnlyList<MusicTrack> Tracks, IReadOnlyList<string> Warnings);
