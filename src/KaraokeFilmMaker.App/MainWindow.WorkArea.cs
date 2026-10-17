using System.Globalization;
using System.Windows;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private void RefreshWorkAreaFields()
    {
        var area = _workspace?.Project.GetWorkArea() ?? default;
        WorkAreaStartText.Text = area.Start.TotalSeconds.ToString("0.######", CultureInfo.CurrentCulture);
        WorkAreaEndText.Text = area.End.TotalSeconds.ToString("0.######", CultureInfo.CurrentCulture);
        WorkAreaDurationText.Text = $"成片 {area.Duration.TotalSeconds:0.###} 秒";
        RefreshWorkAreaAvailability();
    }

    private void RefreshWorkAreaAvailability()
    {
        WorkAreaPanel.IsEnabled = !_isBusy && _workspace?.Project.MasterAudioAssetId is not null;
    }

    private void WorkAreaStartHere_Click(object sender, RoutedEventArgs e) =>
        WorkAreaStartText.Text = _playback.Position.TotalSeconds.ToString("0.######", CultureInfo.CurrentCulture);

    private void WorkAreaEndHere_Click(object sender, RoutedEventArgs e) =>
        WorkAreaEndText.Text = _playback.Position.TotalSeconds.ToString("0.######", CultureInfo.CurrentCulture);

    private async void ApplyWorkArea_Click(object sender, RoutedEventArgs e) => await ApplyWorkAreaAsync(false);
    private async void ResetWorkArea_Click(object sender, RoutedEventArgs e) => await ApplyWorkAreaAsync(true);

    private async Task ApplyWorkAreaAsync(bool reset)
    {
        if (_workspace is null || _isBusy || _workspace.Project.MasterAudioAssetId is null
            || !CanLeaveSourceLyricEdit() || !CanLeaveQuickEdit()) { return; }
        TimeRange? range = null;
        if (!reset)
        {
            double fullLength = _workspace.Project.MediaAssets.First(asset => asset.Id == _workspace.Project.MasterAudioAssetId).Duration.TotalSeconds;
            if (!double.TryParse(WorkAreaStartText.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double start)
                || !double.TryParse(WorkAreaEndText.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double end)
                || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end > fullLength || end <= start
                || ProjectTime.FromSeconds(end) <= ProjectTime.FromSeconds(start))
            {
                StatusText.Text = $"裁切未应用：请输入 0–{fullLength:0.######} 秒内的起止时间，结束须大于开始。";
                return;
            }
            range = new TimeRange(ProjectTime.FromSeconds(start), ProjectTime.FromSeconds(end));
        }
        await RunUiOperationAsync(async () =>
        {
            if (_playback.IsPlaying) { await _playback.PauseAsync(); }
            _workspace = _workspace with { Project = _workspace.Project with { WorkArea = range, ModifiedAtUtc = DateTimeOffset.UtcNow } };
            if (_playback.IsLoaded && IsPreviewMode)
            { await _playback.SeekAsync(_workspace.Project.GetWorkArea().Start); }
            RefreshWorkAreaFields();
            ProjectTimeline.Update(_workspace.Project, _playback.Position);
            RefreshLyricPreview(_playback.Position);
            RefreshVideoPreview(_playback.Position);
            StatusText.Text = reset ? "已恢复歌曲全长，可撤销。"
                : "已裁切成片范围；预览播放与导出使用此范围，素材和歌词打点不变。可撤销或恢复全长。";
        });
    }

    // Paused scrubbing stays unrestricted so either boundary can be extended again.
    // Recording always uses the original song clock and is independent of the output range.
    private async Task PrepareWorkAreaPlaybackAsync()
    {
        if (IsPreviewMode && _workspace?.Project.WorkArea is { } area
            && (_playback.Position < area.Start || _playback.Position >= area.End))
        { await _playback.SeekAsync(area.Start); }
    }

    private async Task EnforceWorkAreaPlaybackAsync()
    {
        if (_isBusy || !_playback.IsPlaying || !IsPreviewMode || _workspace?.Project.WorkArea is not { } area) { return; }
        if (_playback.Position >= area.End)
        {
            await RunUiOperationAsync(async () =>
            {
                await _playback.PauseAsync();
                await _playback.SeekAsync(area.End);
                PlayPauseButton.Content = "播放";
                StatusText.Text = "已到工作区裁切终点。再次播放将从裁切起点开始。";
            });
        }
        else if (_playback.Position < area.Start)
        { await RunUiOperationAsync(() => _playback.SeekAsync(area.Start)); }
    }
}
