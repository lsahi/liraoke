using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Infrastructure.Workspaces;

namespace KaraokeFilmMaker.Infrastructure.Media;

/// <summary>A portable, orientation-normalized PNG shared by preview and export.</summary>
public sealed class ImagePreviewCache(MediaToolchain tools)
{
    public static bool IsSupportedPath(string path) => Path.GetExtension(path).ToLowerInvariant()
        is ".png" or ".jpg" or ".jpeg" or ".bmp";

    public async Task<VideoPreviewFiles> PrepareAsync(string root, MediaAsset asset, CancellationToken cancellationToken = default)
    {
        if (asset.Kind != MediaKind.Image)
        {
            throw new ArgumentException("所选素材不是图片。", nameof(asset));
        }
        string source = WorkspacePaths.AssetPath(root, asset);
        string name = $"image-v3-{asset.Id:N}-{new FileInfo(source).Length}-{File.GetLastWriteTimeUtc(source).Ticks}";
        string proxies = Path.Combine(root, "cache", "proxies");
        string thumbs = Path.Combine(root, "cache", "thumbnails");
        Directory.CreateDirectory(proxies);
        Directory.CreateDirectory(thumbs);
        string proxy = Path.Combine(proxies, name + ".png");
        string thumbnail = Path.Combine(thumbs, name + ".png");
        if (!File.Exists(proxy))
        {
            // This validates content, not only the extension, and rejects animated images/video renamed to PNG.
            await new FfprobeMediaReader(tools).ReadAsync(source, MediaKind.Image, cancellationToken).ConfigureAwait(false);
            // Keep equal detail for portrait, landscape, and square canvases; small sources are enlarged only when displayed.
            await ConvertAsync(source, proxy,
                "scale=w='min(iw,1920)':h='min(ih,1920)':force_original_aspect_ratio=decrease,setsar=1", cancellationToken).ConfigureAwait(false);
        }
        if (!File.Exists(thumbnail))
        {
            await ConvertAsync(proxy, thumbnail, "scale=240:-1", cancellationToken).ConfigureAwait(false);
        }
        return new VideoPreviewFiles(proxy, thumbnail);
    }

    private async Task ConvertAsync(string source, string output, string? filter, CancellationToken cancellationToken)
    {
        string temporary = output + $".{Guid.NewGuid():N}.partial.png";
        try
        {
            List<string> arguments = ["-nostdin", "-v", "error", "-y", "-i", source, "-frames:v", "1"];
            if (filter is not null) { arguments.AddRange(["-vf", filter]); }
            arguments.AddRange(["-map_metadata", "-1", "-pix_fmt", "rgba", "-update", "1", temporary]);
            await MediaProcess.RunAsync(tools.FfmpegPath, arguments, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) { File.Delete(temporary); }
        }
    }
}
