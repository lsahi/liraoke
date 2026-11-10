using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Globalization;
using System.Net.Sockets;
using KaraokeFilmMaker.Application.Music;
using KaraokeFilmMaker.Infrastructure.Media;

namespace KaraokeFilmMaker.Infrastructure.Music;

public sealed class PreparedMusicAudio(string directory, string path, MusicTrack track, string quality, double duration, bool canImport, string availability) : IDisposable
{
    public string DirectoryPath { get; } = directory;
    public string AudioPath { get; } = path;
    public MusicTrack Track { get; } = track;
    public string Quality { get; } = quality;
    public double DurationSeconds { get; } = duration;
    public bool CanImport { get; } = canImport;
    public string Availability { get; } = availability;
    public void Dispose()
    {
        try { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class MusicAudioTransfer(MediaToolchain tools, MusicHostClient host, HttpClient client, string? temporaryRoot = null)
{
    public const long MaximumBytes = 256L * 1024 * 1024;
    public static bool IsComplete(MusicTrack track, double actualSeconds, bool previewOnly) => !previewOnly
        && track.DurationSeconds is > 0 && double.IsFinite(actualSeconds)
        && Math.Abs(actualSeconds - track.DurationSeconds.Value) <= Math.Max(3, track.DurationSeconds.Value * 0.02);

    public async Task<PreparedMusicAudio> PrepareAsync(IMusicSourceAdapter adapter, MusicTrack track,
        MusicSourceCredentials credentials, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if ((adapter.Descriptor.Capabilities & (MusicSourceCapabilities.Preview | MusicSourceCapabilities.ImportAudio)) == 0)
            throw new InvalidOperationException("此音源只提供搜索，未开放音频获取能力。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5)); var token = timeout.Token;
        string directory = Path.Combine(temporaryRoot ?? Path.Combine(Path.GetTempPath(), "Liraoke", "music"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            progress?.Report("正在获取当前版本的音频地址…");
            var resolved = await adapter.ResolveAsync(track, credentials, token).ConfigureAwait(false);
            string raw = Path.Combine(directory, "download.media");
            await DownloadAsync(resolved.Url, raw, progress, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(resolved.DecodeKey))
            {
                if (track.SourceId != "qishui") throw new InvalidDataException("此音源的编码不受支持。");
                string decoded = Path.Combine(directory, "decoded.m4a");
                await host.ExecuteAsync<bool>(new { action = "decode", sourceId = track.SourceId, inputPath = raw,
                    outputPath = decoded, decodeKey = resolved.DecodeKey }, token).ConfigureAwait(false);
                raw = decoded;
            }
            progress?.Report("正在检查音轨、时长和完整性…");
            var metadata = await ProbeAsync(raw, token).ConfigureAwait(false);
            if (!metadata.HasAudio || metadata.Duration.TotalSeconds > 1800) throw new InvalidDataException("未取得有效音轨，或超过首版 30 分钟上限。");
            // Normalize to a local PCM file understood by the existing playback/import pipeline.
            string name = string.Concat(($"{track.Artist} - {track.Title}").Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c));
            name = name[..Math.Min(100, name.Length)].Trim().TrimEnd('.');
            string audio = Path.Combine(directory, $"音频-{name}.wav");
            await MediaProcess.RunAsync(tools.FfmpegPath, ["-nostdin", "-v", "error", "-protocol_whitelist", "file,pipe,crypto", "-format_whitelist", "mp3,wav,flac,mov,ogg,aac", "-i", raw,
                "-map", "0:a:0", "-vn", "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", "-map_metadata", "-1", audio], token).ConfigureAwait(false);
            var decodedInfo = await ProbeAsync(audio, token).ConfigureAwait(false);
            bool complete = adapter.Descriptor.Capabilities.HasFlag(MusicSourceCapabilities.ImportAudio) && IsComplete(track, decodedInfo.Duration.TotalSeconds, resolved.PreviewOnly);
            string status = complete ? "已校验完整时长，可试听或导入" : !adapter.Descriptor.Capabilities.HasFlag(MusicSourceCapabilities.ImportAudio) ? "此音源仅允许试听" : track.DurationSeconds is null
                ? "音源没有提供原曲时长：可试听，暂不导入" : "可能为试听片段或时长不符：仅可试听";
            return new PreparedMusicAudio(directory, audio, track, resolved.Quality, decodedInfo.Duration.TotalSeconds, complete, status);
        }
        catch
        {
            new PreparedMusicAudio(directory, "", track, "", 0, false, "").Dispose();
            throw;
        }
    }

    private async Task<MediaDetails> ProbeAsync(string path, CancellationToken token)
    {
        string json = await MediaProcess.RunAsync(tools.FfprobePath, ["-v", "error", "-protocol_whitelist", "file,pipe,crypto",
            "-format_whitelist", "mp3,wav,flac,mov,ogg,aac", "-show_format", "-show_streams", "-of", "json", path], token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("format", out var format) || !format.TryGetProperty("duration", out var duration)
            || !double.TryParse(duration.GetString(), CultureInfo.InvariantCulture, out double seconds) || !double.IsFinite(seconds) || seconds <= 0)
            throw new InvalidDataException("音源返回的数据不是有效音频。");
        bool audio = doc.RootElement.GetProperty("streams").EnumerateArray().Any(x => x.GetProperty("codec_type").GetString() == "audio");
        return new MediaDetails(KaraokeFilmMaker.Domain.Time.ProjectDuration.FromSeconds(seconds), null, null, null, audio);
    }

    private async Task DownloadAsync(Uri initial, string path, IProgress<string>? progress, CancellationToken token)
    {
        Uri current = initial;
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            PublicMediaHttp.ValidateUri(current);
            using var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                current = response.Headers.Location is { } location ? new Uri(current, location) : throw new InvalidDataException("音源重定向无效。");
                continue;
            }
            response.EnsureSuccessStatusCode();
            long? size = response.Content.Headers.ContentLength;
            if (size > MaximumBytes) throw new InvalidDataException("音频超过 256 MB 上限。");
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920]; long total = 0; int read; long lastReport = -1;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                total += read; if (total > MaximumBytes) throw new InvalidDataException("音频超过 256 MB 上限。");
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                long mb = total / 1048576;
                if (mb != lastReport) { progress?.Report($"正在下载所选音频：{mb} MB" + (size is > 0 ? $" / {size / 1048576} MB" : "")); lastReport = mb; }
            }
            if (total == 0 || size is not null && size != total) throw new InvalidDataException("音频下载不完整。");
            return;
        }
        throw new InvalidDataException("音源重定向次数过多。");
    }
}

// A reusable transport boundary for future video downloads. No cookies or credentials on CDN requests.
public static class PublicMediaHttp
{
    public static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.IsLoopback || !uri.IsDefaultPort || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("音源返回了不支持的下载地址。");
    }
    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;
        byte[] b = ip.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xe0) == 0x20; // Global unicast only.
        return b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 169 && b[1] == 254)
            && !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && b[1] == 168)
            && !(b[0] == 100 && b[1] is >= 64 and <= 127);
    }
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        ConnectCallback = async (context, token) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
            if (addresses.Length == 0 || addresses.Any(ip => !IsPublicAddress(ip))) throw new HttpRequestException("下载地址不是公开媒体服务器。");
            foreach (var ip in addresses)
            {
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(ip, context.DnsEndPoint.Port, token); return new NetworkStream(socket, ownsSocket: true); }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException("无法连接音频服务器。");
        },
    }) { Timeout = Timeout.InfiniteTimeSpan };
}
