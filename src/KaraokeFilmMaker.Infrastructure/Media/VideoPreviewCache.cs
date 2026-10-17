using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Infrastructure.Workspaces;

namespace KaraokeFilmMaker.Infrastructure.Media;

public sealed record VideoPreviewFiles(string ProxyPath, string ThumbnailPath);

public sealed class VideoPreviewCache(MediaToolchain tools)
{
    public async Task<VideoPreviewFiles> PrepareAsync(string root, MediaAsset asset, CancellationToken cancellationToken = default)
    {
        string source = WorkspacePaths.AssetPath(root, asset);
        string name = $"{asset.Id:N}-{new FileInfo(source).Length}-{File.GetLastWriteTimeUtc(source).Ticks}";
        string proxies = Path.Combine(root, "cache", "proxies");
        string thumbs = Path.Combine(root, "cache", "thumbnails");
        Directory.CreateDirectory(proxies); Directory.CreateDirectory(thumbs);
        string proxy = Path.Combine(proxies, name + ".mp4");
        string thumb = Path.Combine(thumbs, name + ".jpg");
        if (!File.Exists(proxy))
        {
            string temporary = proxy + $".{Guid.NewGuid():N}.partial.mp4";
            try
            {
                await MediaProcess.RunAsync(tools.FfmpegPath, ["-nostdin", "-v", "error", "-y", "-i", source,
                    "-an", "-vf", "scale=960:540:force_original_aspect_ratio=decrease:force_divisible_by=2,fps=24,format=yuv420p",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "28", "-movflags", "+faststart", temporary], cancellationToken).ConfigureAwait(false);
                File.Move(temporary, proxy, overwrite: true);
            }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        }
        if (!File.Exists(thumb))
        {
            string temporary = thumb + $".{Guid.NewGuid():N}.partial.jpg";
            try
            {
                await MediaProcess.RunAsync(tools.FfmpegPath, ["-nostdin", "-v", "error", "-y", "-i", proxy,
                    "-frames:v", "1", "-vf", "scale=240:-2", temporary], cancellationToken).ConfigureAwait(false);
                File.Move(temporary, thumb, overwrite: true);
            }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        }
        return new VideoPreviewFiles(proxy, thumb);
    }
}
