using System.IO;
using System.Windows;
using KaraokeFilmMaker.Infrastructure.Separation;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private bool _previewAccompaniment;
    private Guid? _accompanimentProjectId;
    private Guid? _accompanimentSourceId;

    private bool HasCurrentAccompaniment => _workspace is not null
        && AccompanimentStore.IsAvailable(_workspace.RootPath, _workspace.Project);

    private bool UsePreviewAccompaniment => IsPreviewMode && _previewAccompaniment && HasCurrentAccompaniment;

    private void RefreshAccompanimentState()
    {
        if (AccompanimentPreviewCheck is null) return;
        if (_accompanimentProjectId != _workspace?.Project.Id || _accompanimentSourceId != _workspace?.Project.MasterAudioAssetId)
            _previewAccompaniment = false;
        _accompanimentProjectId = _workspace?.Project.Id;
        _accompanimentSourceId = _workspace?.Project.MasterAudioAssetId;
        bool available = HasCurrentAccompaniment;
        if (!available) _previewAccompaniment = false;
        AccompanimentPreviewCheck.IsChecked = _previewAccompaniment;
        AccompanimentPreviewCheck.IsEnabled = available && !_isBusy;
        AccompanimentPreviewCheck.ToolTip = available ? "仅切换预览声音；打轴使用原歌曲，导出单独选择版本。"
            : _workspace?.Project.GetAccompaniment(_workspace.Project.MasterAudioAssetId) is not null
                ? "关联伴奏文件丢失，请重新制作伴奏。" : "当前歌曲还没有伴奏，请先点击顶部“制作伴奏”。";
        MakeAccompanimentButton.IsEnabled = _workspace?.Project.MasterAudioAssetId is not null && !_isBusy;
        MakeAccompanimentButton.ToolTip = available ? "当前歌曲已生成伴奏；点击可重新制作。" : "为当前歌曲制作伴奏。";
    }

    private async void AccompanimentPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || !HasCurrentAccompaniment) { RefreshAccompanimentState(); return; }
        bool previous = _previewAccompaniment;
        bool requested = AccompanimentPreviewCheck.IsChecked == true;
        await RunUiOperationAsync(async () =>
        {
            _previewAccompaniment = requested;
            try { await PrepareProjectPlaybackAsync(); }
            catch { _previewAccompaniment = previous; _preparedPlaybackKey = null; throw; }
            StatusText.Text = _previewAccompaniment ? "预览已切换为伴奏；打轴与导出选择不变。" : "预览已切换为原唱。";
        });
        RefreshAccompanimentState();
    }

    private async void VocalSeparation_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || _isBusy) { StatusText.Text = "请先打开工作区并导入歌曲。"; return; }
        if (!CanLeaveSourceLyricEdit() || !CanLeaveQuickEdit()) return;
        if (_markers.Count > 0 || _undoneMarkers.Count > 0)
        { StatusText.Text = "请先应用或明确清空本次打点，再制作伴奏；已保存的歌词时间不会改变。"; return; }
        var source = _workspace.Project.MediaAssets.FirstOrDefault(a => a.Id == _workspace.Project.MasterAudioAssetId);
        if (source is null) { StatusText.Text = "请先导入歌曲。"; return; }
        if (HasCurrentAccompaniment && MessageBox.Show(this,
            "当前歌曲已有伴奏，是否重新制作并更新关联？原歌曲和歌词打轴不变，失败或取消保留已有伴奏。",
            "重新制作伴奏", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        if (_playback.IsPlaying) await _playback.PauseAsync();
        VideoPreview.Pause();
        PlayPauseButton.Content = "播放";
        var window = new VocalSeparationWindow(Path.Combine(_workspace.RootPath, source.RelativePath), source.DisplayName,
            Path.Combine(_workspace.RootPath, "cache", "separation"), _mediaTools) { Owner = this };
        if (window.ShowDialog() != true || window.GeneratedPath is not { } generated) return;
        await RunUiOperationAsync(async () =>
        {
            var project = await new AccompanimentStore(_mediaTools).RegisterAsync(_workspace.RootPath,
                _workspace.Project, source.Id, generated, _operationCancellation!.Token);
            _workspace = _workspace with { Project = project };
            RefreshAccompanimentState();
            await PrepareProjectPlaybackAsync();
            // Registered copies remain on disk for undo. Only discard this task's temporary result.
            try { File.Delete(generated); } catch (IOException) { }
            StatusText.Text = "伴奏已关联到当前歌曲。可在预览右侧“伴奏”页开启试听；请保存工程以保留关联。";
        });
    }
}
