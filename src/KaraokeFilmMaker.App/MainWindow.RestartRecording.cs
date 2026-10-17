using System.Windows;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private async void RestartRecording_Click(object sender, RoutedEventArgs e) => await RestartRecordingAsync();

    private async Task RestartRecordingAsync(Func<bool>? confirm = null)
    {
        if (_workspace is null || _isBusy || !CanLeaveSourceLyricEdit() || !CanLeaveQuickEdit()) { return; }
        bool accepted = confirm?.Invoke() ?? MessageBox.Show(this,
            "重新打轴将清空整首歌词的所有时间点，包括已应用的行／单元时间、本次待应用打点及可重做打点。\n\n"
            + "已修改的歌词文字、读音、罗马音、分组和语言标记全部保留；不会改动素材、视频片段或工作区裁切范围。\n\n确定重新打轴吗？",
            "重新打轴", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!accepted) { return; }
        await RunUiOperationAsync(async () =>
        {
            if (_playback.IsPlaying) { await _playback.PauseAsync(); }
            // Do not re-import, regroup or re-analyze lyrics: editing and timing are independent.
            _workspace = _workspace with
            {
                Project = _workspace.Project with
                { Lyrics = LyricTimingService.ClearAllTimings(_workspace.Project.Lyrics), ModifiedAtUtc = DateTimeOffset.UtcNow },
            };
            ResetMarkers();
            _loopRange = null; _countdownTarget = null;
            LoopLineButton.Content = "循环选中歌词行";
            PlayPauseButton.Content = "播放";
            if (_playback.IsLoaded) { await _playback.SeekAsync(ProjectTime.Zero); }
            LyricsGrid.SelectedIndex = _workspace.Project.Lyrics.Lines.Count > 0 ? 0 : -1;
            RefreshLyricRows(); RefreshMarkerList();
            EditorTabs.SelectedItem = RecordingTab;
            ProjectTimeline.Update(_workspace.Project, _playback.Position);
            ProjectTimeline.Focus();
            StatusText.Text = "已清空全部歌词时间点，从头重新打轴；歌词文字、读音和分组修改均保留。保存项目可持久化此修改。";
        });
    }
}
