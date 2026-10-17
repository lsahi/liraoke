using System.Windows;
using KaraokeFilmMaker.App.Dialogs;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Workspaces;
using Microsoft.Win32;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private async void ImportImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || _isBusy) { return; }
        var dialog = new OpenFileDialog
        {
            Title = "导入图片到工作区",
            Filter = "静态图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (!_dialogLocations.ShowDialog(dialog, DialogLocation.ImportImage, this)) { return; }
        await ImportImageAsync(dialog.FileName);
    }

    private async Task ImportImageAsync(string sourcePath)
    {
        if (_workspace is null || _isBusy) { return; }
        await RunUiOperationAsync(async () =>
        {
            ApplyLyricRowsToProject(); ApplyVideoRowsToProject();
            var session = await _workspaceService.ImportMediaAsync(_workspace, sourcePath, MediaKind.Image, _operationCancellation!.Token);
            var imported = session.Project.VideoClips.Last();
            var asset = session.Project.MediaAssets.First(asset => asset.Id == imported.MediaAssetId);
            VideoPreviewFiles preview;
            try
            {
                preview = await new ImagePreviewCache(_mediaTools).PrepareAsync(session.RootPath, asset, _operationCancellation.Token);
                _operationCancellation.Token.ThrowIfCancellationRequested();
            }
            catch
            {
                // Only this newly created copy is removed; the user's original image is never touched.
                System.IO.File.Delete(WorkspacePaths.AssetPath(session.RootPath, asset));
                throw;
            }
            _workspace = session;
            _previewPaths[asset.Id] = preview.ProxyPath;
            _thumbnailPaths[asset.Id] = preview.ThumbnailPath;
            RefreshVideoRows(); RefreshWorkspaceDisplay();
            VideoGrid.SelectedItem = _videoRows.First(row => row.Id == imported.Id);
            StatusText.Text = "图片已复制到工作区并加入视频轨，默认显示 5 秒；拖动片段两端可调整显示时长。";
        });
    }
}
