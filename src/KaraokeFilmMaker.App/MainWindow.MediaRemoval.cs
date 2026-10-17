using System.Windows;
using KaraokeFilmMaker.Application.Media;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private async void RemoveMediaAsset_Click(object sender, RoutedEventArgs e)
    {
        if (MediaLibraryList.SelectedItem is not LibraryItem item)
        { StatusText.Text = "请先选择要移除的素材。"; return; }
        await RemoveMediaAssetAsync(item.Id);
    }

    private async Task RemoveMediaAssetAsync(Guid assetId, Func<MediaRemovalImpact, bool>? confirm = null)
    {
        if (_workspace is null || _isBusy || !CanLeaveSourceLyricEdit() || !CanLeaveQuickEdit()) { return; }
        if (_markers.Count > 0 || _undoneMarkers.Count > 0)
        { StatusText.Text = "请先应用或撤销内存中的打点，再移除素材。"; return; }
        MediaRemovalImpact impact = MediaAssetRemoval.Inspect(_workspace.Project, assetId);
        if (impact.RequiresConfirmation && !(confirm?.Invoke(impact) ?? ConfirmMediaRemoval(impact))) { return; }
        await RunUiOperationAsync(async () =>
        {
            ApplyLyricRowsToProject();
            ApplyVideoRowsToProject();
            ProjectTime previousPosition = _playback.Position;
            _workspace = _workspace with { Project = MediaAssetRemoval.Remove(_workspace.Project, assetId, true) };
            // Files are deliberately retained for undo, so retain their cache lookups too.
            // The library and preview select only assets still present in the project.
            // Undo with the same master audio does not rebuild the preview cache.
            _failedPreviewAssets.Remove(assetId);
            if (impact.RequiresConfirmation)
            {
                await ResetPlaybackAsync();
                await LoadMasterAudioIfAvailableAsync();
                if (_playback.IsLoaded) { await _playback.SeekAsync(previousPosition); }
            }
            RefreshVideoRows();
            RefreshWorkspaceDisplay();
            RefreshLyricPreview(_playback.Position);
            StatusText.Text = $"已移除素材“{impact.Asset.DisplayName}”及关联轨道内容；歌词打轴保留，可撤销。工作区中的素材文件未删除。";
        });
    }

    private bool ConfirmMediaRemoval(MediaRemovalImpact impact)
    {
        string uses = (impact.IsMasterAudio ? "\n• 当前歌曲 / 声音" : "")
            + (impact.VideoClipCount > 0 ? $"\n• 视频轨道中的 {impact.VideoClipCount} 个片段" : "");
        return MessageBox.Show(this, $"素材“{impact.Asset.DisplayName}”正在使用。移除时将同时清除：{uses}\n\n已打好的歌词时间轴不会改变。此操作可撤销，工作区中的素材文件不会删除。\n是否继续？",
            "移除素材", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
