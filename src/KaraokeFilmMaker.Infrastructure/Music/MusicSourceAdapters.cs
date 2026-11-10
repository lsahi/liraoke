using KaraokeFilmMaker.Application.Music;

namespace KaraokeFilmMaker.Infrastructure.Music;

public abstract class LocalMusicSourceAdapter(MusicHostClient host) : IMusicSourceAdapter
{
    public abstract MusicSourceDescriptor Descriptor { get; }
    public async Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, MusicSourceCredentials credentials, CancellationToken cancellationToken = default) =>
        await host.ExecuteAsync<MusicTrack[]>(new { action = "search", sourceId = Descriptor.Id, query, cookie = credentials.Cookie }, cancellationToken);
    public Task<ResolvedMusicAudio> ResolveAsync(MusicTrack track, MusicSourceCredentials credentials, CancellationToken cancellationToken = default)
    {
        if (track.SourceId != Descriptor.Id) throw new ArgumentException("歌曲与音源不匹配。");
        return host.ExecuteAsync<ResolvedMusicAudio>(new { action = "resolve", sourceId = Descriptor.Id, id = track.Id,
            cookie = credentials.Cookie, signerEndpoint = credentials.SignerEndpoint }, cancellationToken);
    }
    protected const MusicSourceCapabilities Standard = MusicSourceCapabilities.Search | MusicSourceCapabilities.Preview | MusicSourceCapabilities.ImportAudio | MusicSourceCapabilities.Account;
}
public sealed class NeteaseMusicSourceAdapter(MusicHostClient host) : LocalMusicSourceAdapter(host)
{
    public override MusicSourceDescriptor Descriptor { get; } = new("netease", "网易云音乐", Standard, MediaAuthenticationKind.Cookie);
}
public sealed class TencentMusicSourceAdapter(MusicHostClient host) : LocalMusicSourceAdapter(host)
{
    public override MusicSourceDescriptor Descriptor { get; } = new("tencent", "QQ 音乐", Standard, MediaAuthenticationKind.Cookie);
}
public sealed class QishuiMusicSourceAdapter(MusicHostClient host) : LocalMusicSourceAdapter(host)
{
    public override MusicSourceDescriptor Descriptor { get; } = new("qishui", "汽水音乐", Standard, MediaAuthenticationKind.Cookie,
        "公开搜索无需登录。获取音频需要账号 Cookie 和可信签名服务；不自动更换为其他版本。 ");
}
