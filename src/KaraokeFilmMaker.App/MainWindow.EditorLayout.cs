using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private bool IsRecordingMode => EditorTabs.SelectedItem == RecordingTab;
    private bool IsPreviewMode => EditorTabs.SelectedItem == PreviewTab;
    private bool _syncingLyricSelection;

    private async void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != EditorTabs || !IsInitialized || SharedMonitor is null) { return; }
        if (EditorTabs.SelectedItem != LyricSourceTab && !CanLeaveSourceLyricEdit())
        { EditorTabs.SelectedItem = LyricSourceTab; return; }
        UpdateEditorMode();
        if (_previewAccompaniment && _playback.IsLoaded && !_isBusy)
            await RunUiOperationAsync(() => PrepareProjectPlaybackAsync());
    }

    private void UpdateEditorMode()
    {
        if (SharedMonitor.Parent is ContentControl previous) { previous.Content = null; }
        // Keep one video backend and one subtitle renderer when switching workspaces.
        ClearVideoPreview();
        (IsRecordingMode ? RecordingMonitorHost : PreviewMonitorHost).Content = SharedMonitor;
        SubtitlePreview.Visibility = IsPreviewMode ? Visibility.Visible : Visibility.Collapsed;
        RefreshAudioVisualizationPreview(_playback.Position);
        RefreshLyricPreview(_playback.Position);
        ProjectTimeline.ShowLyricUnits = IsRecordingMode;
        TimelineRow.Height = new GridLength(IsPreviewMode ? 344 : IsRecordingMode ? 180 : 280);
        WorkAreaPanel.Visibility = IsPreviewMode ? Visibility.Visible : Visibility.Collapsed;
        ExportFormatPanel.Visibility = IsRecordingMode ? Visibility.Collapsed : Visibility.Visible;
        ClipToolsPanel.Visibility = IsPreviewMode ? Visibility.Visible : Visibility.Collapsed;
        ProjectTimeline.Update(_workspace?.Project, _playback.Position);
        MonitorShortcutText.Text = IsRecordingMode
            ? "空格：下一个字 / 单词 / 音节　退格：回退一单元　Enter：结束句并应用　P：播放 / 暂停"
            : "空格：播放 / 暂停　← →：逐帧　J：回退 1 秒　K：暂停　L：播放";
        TimelineModeText.Text = IsRecordingMode ? "歌词打轴 · 一次空格一个演唱单元" : "歌曲 · 视频 · 字幕";
        if (!IsRecordingMode)
        {
            _loopRange = null; _countdownTarget = null;
            LoopLineButton.Content = "循环选中歌词行";
        }
        RefreshTapHint(_playback.Position);
    }

    private void LyricsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingLyricSelection) { return; }
        _syncingLyricSelection = true;
        try { RecordingLineCombo.SelectedItem = LyricsGrid.SelectedItem; }
        finally { _syncingLyricSelection = false; }
    }

    private void RecordingLineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingLyricSelection || !IsInitialized) { return; }
        bool explicitSelection = RecordingLineCombo.IsKeyboardFocusWithin && RecordingLineCombo.SelectedIndex >= 0
            && _markers.Count == 0 && _undoneMarkers.Count == 0;
        if (explicitSelection && !CanLeaveQuickEdit())
        {
            _syncingLyricSelection = true;
            try { RecordingLineCombo.SelectedItem = LyricsGrid.SelectedItem; }
            finally { _syncingLyricSelection = false; }
            return;
        }
        _syncingLyricSelection = true;
        try { LyricsGrid.SelectedItem = RecordingLineCombo.SelectedItem; }
        finally { _syncingLyricSelection = false; }
        if (explicitSelection)
        {
            ResetMarkers();
            _rerecordFromSelection = true;
            RefreshMarkerList();
            StatusText.Text = "已选择重录起点；应用后替换本次录制句子的时间。可点“续录未完成”返回原进度。";
        }
    }

    private void RecordingSelector_DropDownClosed(object? sender, EventArgs e)
    {
        if (IsRecordingMode && !_isBusy) { ProjectTimeline.Focus(); }
    }

    private void Monitor_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => ProjectTimeline.Focus();

    private void VideoGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { ProjectTimeline?.SelectClip((VideoGrid.SelectedItem as VideoClipEditorRow)?.Id); }

    private void ApplyCurrentMarkersButton_Click(object sender, RoutedEventArgs e)
    {
        if (TapModeCombo.SelectedIndex == 1) { ApplyUnitMarkersButton_Click(sender, e); }
        else { ApplyLineMarkersButton_Click(sender, e); }
    }

    private void RefreshMediaLibrary()
    {
        if (MediaLibraryList is null) { return; }
        Guid? selected = (MediaLibraryList.SelectedItem as LibraryItem)?.Id;
        MediaLibraryList.ItemsSource = _workspace?.Project.MediaAssets.Select(asset => new LibraryItem(asset.Id,
            asset.DisplayName, asset.Kind == MediaKind.Image ? "图片 · 时长由片段决定"
                : $"{(asset.Kind == MediaKind.Video ? "视频" : "音频")} · {asset.Duration.TotalSeconds:0.0} 秒",
            _thumbnailPaths.GetValueOrDefault(asset.Id), asset.Kind)).ToArray();
        MediaLibraryList.SelectedItem = MediaLibraryList.Items.Cast<LibraryItem>().FirstOrDefault(item => item.Id == selected);
    }

    private async void MediaLibraryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_workspace is null || MediaLibraryList.SelectedItem is not LibraryItem item) { return; }
        var asset = _workspace.Project.MediaAssets.First(media => media.Id == item.Id);
        if (asset.Kind is not (MediaKind.Video or MediaKind.Image)) { StatusText.Text = "主歌曲已位于声音轨；视频和图片素材可重复添加到视频轨道。"; return; }
        await RunUiOperationAsync(() =>
        {
            ApplyVideoRowsToProject();
            const int track = 0;
            ProjectDuration duration = asset.Kind == MediaKind.Image ? KaraokeFilmMaker.Application.Timeline.VideoTimelineService.DefaultImageDuration : asset.Duration;
            ProjectTime start = _playback.Position;
            if (_workspace.Project.VideoClips.Any(clip => clip.TrackIndex == track && clip.TimelineStart < start + duration && start < clip.TimelineEnd))
            {
                start = _workspace.Project.VideoClips.Where(clip => clip.TrackIndex == track).MaxBy(clip => clip.TimelineEnd)!.TimelineEnd;
            }
            var clip = new VideoClip(Guid.NewGuid(), asset.Id, start, ProjectTime.Zero, duration, true, track);
            _workspace = _workspace with { Project = _workspace.Project with { VideoClips = [.. _workspace.Project.VideoClips, clip] } };
            RefreshVideoRows();
            VideoGrid.SelectedItem = _videoRows.First(row => row.Id == clip.Id);
            StatusText.Text = "素材已加入视频轨道；无需再次复制文件。";
            return Task.CompletedTask;
        });
    }

    private async void SplitSelectedClipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || VideoGrid.SelectedItem is not VideoClipEditorRow row) { return; }
        await RunUiOperationAsync(() =>
        {
            ApplyVideoRowsToProject();
            _workspace = _workspace with { Project = _videoTimelineService.SplitClip(_workspace.Project, row.Id, _playback.Position) };
            RefreshVideoRows();
            StatusText.Text = "已在播放头处分割片段，原声和素材入点保持绑定，可撤销。";
            return Task.CompletedTask;
        });
    }

    private async void ToggleSelectedMuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || VideoGrid.SelectedItem is not VideoClipEditorRow row) { return; }
        if (!row.HasSourceTiming) { StatusText.Text = "图片没有原声，无需切换静音。"; return; }
        await RunUiOperationAsync(() =>
        {
            ApplyVideoRowsToProject();
            var clip = _workspace.Project.VideoClips.First(item => item.Id == row.Id);
            _workspace = _workspace with { Project = _videoTimelineService.UpdateClip(_workspace.Project, clip.Id,
                clip.TimelineStart, clip.SourceStart, clip.Duration, !clip.IsMuted) };
            RefreshVideoRows();
            StatusText.Text = clip.IsMuted ? "已启用片段原声。" : "片段原声已静音。";
            return Task.CompletedTask;
        });
    }

    private void FitTimelineButton_Click(object sender, RoutedEventArgs e) => ProjectTimeline.FitToProject();
    private async void GoToStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playback.IsLoaded) { await RunUiOperationAsync(() => _playback.SeekAsync(
            IsPreviewMode ? _workspace?.Project.GetWorkArea().Start ?? ProjectTime.Zero : ProjectTime.Zero)); }
    }
    private sealed record LibraryItem(Guid Id, string Name, string Details, string? ThumbnailPath, MediaKind Kind);
}
