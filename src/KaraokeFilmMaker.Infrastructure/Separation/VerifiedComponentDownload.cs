using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using KaraokeFilmMaker.Application.Separation;

namespace KaraokeFilmMaker.Infrastructure.Separation;

/// <summary>Resumable bytes are never executable until size and SHA-256 both match.</summary>
public sealed class VerifiedComponentDownload(HttpClient client)
{
    public async Task<string> DownloadAsync(Uri uri, long size, string sha256, string cacheFile,
        IProgress<SeparationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!GpuRuntimeManifest.IsOfficialUrl(uri)) throw new InvalidDataException("拒绝非官方 GPU 下载地址。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cacheFile))!);
        if (File.Exists(cacheFile) && await MatchesAsync(cacheFile, size, sha256, cancellationToken)) return cacheFile;
        string partial = cacheFile + ".partial";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (existing > size) { File.Delete(partial); existing = 0; }
                if (existing != size)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
                    using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    headerTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (!GpuRuntimeManifest.IsOfficialUrl(response.RequestMessage?.RequestUri ?? uri))
                        throw new InvalidDataException("GPU 下载被重定向到非官方地址。");
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (range?.From != existing || range.Length != size || range.To != size - 1)
                            throw new InvalidDataException("续传响应范围不匹配。");
                    }
                    else if (response.StatusCode == HttpStatusCode.OK) existing = 0;
                    else throw new InvalidDataException("无效下载响应。");
                    if (response.Content.Headers.ContentLength is long length && length != size - existing)
                        throw new InvalidDataException("下载长度与固定组件清单不一致。");
                    await using var destination = new FileStream(partial, existing == 0 ? FileMode.Create : FileMode.Append,
                        FileAccess.Write, FileShare.None, 128 * 1024, true);
                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    byte[] buffer = new byte[128 * 1024];
                    long received = existing;
                    var nextReport = DateTime.UtcNow;
                    while (true)
                    {
                        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        readTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                        int count = await source.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        if (received + count > size) throw new InvalidDataException("下载超过固定组件大小。");
                        await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        received += count;
                        if (DateTime.UtcNow >= nextReport)
                        {
                            progress?.Report(new($"下载 GPU 组件：{received / 1048576d:0} / {size / 1048576d:0} MiB", (double)received / size * .65));
                            nextReport = DateTime.UtcNow.AddMilliseconds(200);
                        }
                    }
                    if (received != size) throw new IOException("下载中断；已保留进度，可重试续传。");
                }
                progress?.Report(new("校验官方 SHA-256…", .67));
                if (!await MatchesAsync(partial, size, sha256, cancellationToken))
                {
                    File.Delete(partial);
                    throw new InvalidDataException("GPU 文件校验失败，未安装。请重试下载。");
                }
                File.Move(partial, cacheFile, true);
                return cacheFile;
            }
            catch (Exception e) when (attempt < 2 && !cancellationToken.IsCancellationRequested
                && (e is HttpRequestException or IOException or OperationCanceledException))
            {
                progress?.Report(new("连接中断，正在重试续传…"));
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
            { throw new IOException("下载连接超时，已保留可续传进度，请稍后重试。", e); }
        }
        throw new IOException("下载失败，可稍后重试。");
    }

    public static async Task<bool> MatchesAsync(string file, long size, string sha256, CancellationToken token)
    {
        if (!File.Exists(file) || new FileInfo(file).Length != size) return false;
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }
}
