using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Infrastructure.Media;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private readonly Dictionary<Guid, string> _previewPaths = [];
    private readonly Dictionary<Guid, string> _thumbnailPaths = [];

    private async Task PreparePreviewsAsync()
    {
        if (_workspace is null) { return; }
        _previewPaths.Clear(); _thumbnailPaths.Clear();
        foreach (var asset in _workspace.Project.MediaAssets.Where(asset => asset.Kind == MediaKind.Video))
        {
            StatusText.Text = $"准备低分辨率预览：{asset.DisplayName}";
            VideoPreviewFiles files = await new VideoPreviewCache(_mediaTools).PrepareAsync(_workspace.RootPath, asset,
                _operationCancellation?.Token ?? CancellationToken.None);
            _previewPaths[asset.Id] = files.ProxyPath;
            _thumbnailPaths[asset.Id] = files.ThumbnailPath;
        }
        foreach (var asset in _workspace.Project.MediaAssets.Where(asset => asset.Kind == MediaKind.Image))
        { await PrepareImagePreviewAsync(asset); }
    }

    private async Task PrepareImagePreviewAsync(MediaAsset asset)
    {
        if (_workspace is null) { return; }
        StatusText.Text = $"准备图片预览：{asset.DisplayName}";
        VideoPreviewFiles files = await new ImagePreviewCache(_mediaTools).PrepareAsync(_workspace.RootPath, asset,
            _operationCancellation?.Token ?? CancellationToken.None);
        _previewPaths[asset.Id] = files.ProxyPath;
        _thumbnailPaths[asset.Id] = files.ThumbnailPath;
    }
}
