using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Audio;
using KaraokeFilmMaker.Infrastructure.Export;
using KaraokeFilmMaker.Infrastructure.Media;
using Microsoft.Win32;
using KaraokeFilmMaker.App.Dialogs;
using KaraokeFilmMaker.Application.Separation;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private readonly MediaToolchain _mediaTools = MediaToolchain.Find();
    private double _playbackRate = 1;
    private string? _preparedPlaybackKey;
    private CancellationTokenSource? _operationCancellation;
    private bool _syncingExportFormat;

    private async Task PrepareProjectPlaybackAsync(bool force = false)
    {
        if (_workspace?.Project.MasterAudioAssetId is not { } masterId)
        {
            return;
        }

        var project = _workspace.Project;
        RefreshAccompanimentState();
        bool accompaniment = UsePreviewAccompaniment;
        var master = project.MediaAssets.First(asset => asset.Id == masterId);
        string key = JsonSerializer.Serialize(new { Master = masterId, project.MediaAssets, project.VideoClips,
            Accompaniment = accompaniment ? project.GetAccompaniment(masterId) : null, Rate = _playbackRate });
        if (!force && key == _preparedPlaybackKey && _playback.IsLoaded)
        {
            return;
        }

        ProjectTime position = force ? ProjectTime.Zero : _playback.Position;
        bool resume = !force && _playback.IsPlaying;
        if (_playback.IsPlaying) { await _playback.PauseAsync(); }
        StatusText.Text = "正在准备歌曲、视频原声和播放缓存…";
        string wave = await new ProjectAudioComposer(_mediaTools).PrepareAsync(_workspace.RootPath, project, _playbackRate,
            _operationCancellation?.Token ?? CancellationToken.None, accompaniment: accompaniment);
        await ((ScaledAudioPlaybackSession)_playback).LoadPreparedAsync(wave, master.Duration, _playbackRate);
        await SynchronizeMetronomeAsync();
        await _playback.SeekAsync(position);
        if (resume) { await _playback.PlayAsync(); }
        _preparedPlaybackKey = key;
        PositionSlider.Maximum = Math.Max(0.001, master.Duration.TotalSeconds);
        PositionSlider.IsEnabled = true;
        VideoPreview.SpeedRatio = _playbackRate;
        string normalWave = _playbackRate == 1 ? wave
            : await new ProjectAudioComposer(_mediaTools).PrepareAsync(_workspace.RootPath, project,
                cancellationToken: _operationCancellation?.Token ?? CancellationToken.None, accompaniment: accompaniment);
        var waveform = await new WaveformCache().GetAsync(normalWave, _workspace.RootPath,
            _operationCancellation?.Token ?? CancellationToken.None);
        ProjectTimeline.Waveform = waveform;
    }

    private async void PlaybackRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || PlaybackRateCombo.SelectedItem is not ComboBoxItem item
            || !double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double rate))
        {
            return;
        }

        _playbackRate = rate;
        if (_workspace is not null && _playback.IsLoaded)
        {
            await RunUiOperationAsync(async () =>
            {
                await PrepareProjectPlaybackAsync();
                StatusText.Text = $"播放速度：{_playbackRate:0.##}×，歌曲时间轴保持原始时间。";
            });
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || _isBusy) { return; }
        if (!CanLeaveSourceLyricEdit() || !CanLeaveQuickEdit()) return;
        if (_markers.Count > 0 || _undoneMarkers.Count > 0)
        {
            EditorTabs.SelectedItem = RecordingTab;
            StatusText.Text = "还有未应用的内存打点。请完成当前句并按 Enter 确认结束点后再导出，避免遗漏刚录制的时间。";
            return;
        }
        var versionsDialog = new ExportVersionsWindow(HasCurrentAccompaniment) { Owner = this };
        if (versionsDialog.ShowDialog() != true) return;
        var dialog = new SaveFileDialog
        {
            Title = "选择导出文件名（自动追加版本后缀）", Filter = "MP4 视频 (*.mp4)|*.mp4",
            FileName = _workspace.Project.Title + ".mp4", AddExtension = true, OverwritePrompt = false,
        };
        if (!_dialogLocations.ShowDialog(dialog, DialogLocation.ExportVideo, this,
            System.IO.Path.Combine(_workspace.RootPath, "exports"))) { return; }
        var versions = AudioExportVersions.Create(dialog.FileName, versionsDialog.OriginalCheck.IsChecked == true,
            versionsDialog.AccompanimentCheck.IsEnabled && versionsDialog.AccompanimentCheck.IsChecked == true);
        var existing = versions.Where(item => System.IO.File.Exists(item.Path)).ToArray();
        if (existing.Length > 0 && MessageBox.Show(this, "以下文件已存在，是否覆盖？\n\n" + string.Join("\n", existing.Select(item => item.Path)),
            "确认覆盖导出", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await RunUiOperationAsync(async () =>
        {
            ApplyLyricRowsToProject();
            ApplyVideoRowsToProject();
            ApplySubtitleStyleFields();
            ApplyExportPreset();
            if (_playback.IsPlaying) { await _playback.PauseAsync(); }
            for (int index = 0; index < versions.Count; index++)
            {
                var version = versions[index]; int ordinal = index;
                string label = version.Accompaniment ? "伴奏" : "原唱";
                bool reporting = true;
                var progress = new Progress<ExportProgress>(value =>
                {
                    if (!reporting) return;
                    OperationProgress.Value = (ordinal + value.Fraction) / versions.Count * 100;
                    StatusText.Text = $"{label} {ordinal + 1}/{versions.Count} · {value.Message} · {value.Fraction:P0}";
                });
                try
                {
                    await new VideoExportService(_mediaTools).ExportAsync(_workspace.RootPath, _workspace.Project, version.Path,
                        progress, _operationCancellation!.Token, accompaniment: version.Accompaniment);
                }
                catch (OperationCanceledException)
                {
                    _cancelledExportSummary = index == 0 ? "导出已取消，不完整输出已清理。"
                        : "导出已取消；已完成的版本保留：" + string.Join("；", versions.Take(index).Select(item => item.Path));
                    throw;
                }
                catch (Exception exception) when (index > 0)
                {
                    throw new InvalidOperationException($"{label}导出失败：{exception.Message}\n已完成的版本保留："
                        + string.Join("；", versions.Take(index).Select(item => item.Path)), exception);
                }
                finally { reporting = false; }
            }
            OperationProgress.Value = 100;
            StatusText.Text = "已导出：" + string.Join("；", versions.Select(item => item.Path));
        });
    }

    private string? _cancelledExportSummary;

    private void ApplyExportPreset()
    {
        if (_workspace is null) { return; }
        _workspace = _workspace with
        {
            Project = _workspace.Project with
            {
                ExportPreset = SelectedExportPreset(),
                InputOffsetMilliseconds = int.TryParse(InputOffsetText.Text, out int offset) ? Math.Clamp(offset, -2000, 2000) : 0,
            },
        };
        RefreshPreviewCanvas();
    }

    private ExportPreset SelectedExportPreset()
    {
        string ratio = VideoOutputFormats.AspectRatios[Math.Clamp(ExportAspectRatioCombo.SelectedIndex, 0, 3)];
        int resolution = ExportResolutionCombo.SelectedIndex == 1 ? 720 : 1080;
        int fps = ExportFrameRateCombo.SelectedIndex switch { 0 => 24, 2 => 60, _ => 30 };
        return VideoOutputFormats.ApplyTo(_workspace!.Project.ExportPreset, ratio, resolution) with { FramesPerSecond = fps };
    }

    private void ExportFormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _syncingExportFormat || _workspace is null) { return; }
        if (_isBusy) { RefreshExportPreset(); return; }
        ExportPreset selected = SelectedExportPreset();
        if (selected == _workspace.Project.ExportPreset) { return; }
        var before = _workspace;
        _workspace = _workspace with
        {
            Project = _workspace.Project with { ExportPreset = selected, ModifiedAtUtc = DateTimeOffset.UtcNow },
        };
        RecordHistory(before);
        RefreshPreviewCanvas();
        RefreshLyricPreview(_playback.Position);
        StatusText.Text = $"画幅已调整为 {selected.Width} × {selected.Height}，预览与导出同步；图片等比例铺满并居中裁切。";
    }

    private void RefreshPreviewCanvas()
    {
        var preset = _workspace?.Project.ExportPreset;
        PreviewCanvas.Width = preset?.Width ?? 1920;
        PreviewCanvas.Height = preset?.Height ?? 1080;
        ExportDimensionsText.Text = $"{PreviewCanvas.Width:0} × {PreviewCanvas.Height:0}";
        RefreshPreviewMonitorSize();
    }

    private void RefreshExportFormatAvailability()
    {
        bool enabled = _workspace is not null && !_isBusy;
        ExportAspectRatioCombo.IsEnabled = enabled;
        ExportResolutionCombo.IsEnabled = enabled;
        ExportFrameRateCombo.IsEnabled = enabled;
    }

    private void RefreshExportPreset()
    {
        RefreshWorkAreaFields();
        RefreshMetronomeFields();
        RefreshAudioVisualizationFields();
        RefreshExportFormatAvailability();
        RefreshPreviewCanvas();
        if (_workspace is null) { return; }
        _syncingExportFormat = true;
        try
        {
            var preset = _workspace.Project.ExportPreset;
            if (!VideoOutputFormats.TryIdentify(preset.Width, preset.Height, out string ratio, out int resolution))
            {
                ratio = preset.Width < preset.Height ? "9:16" : preset.Width == preset.Height ? "1:1"
                    : Math.Abs((double)preset.Width / preset.Height - 4d / 3) < 0.01 ? "4:3" : "16:9";
                resolution = Math.Min(preset.Width, preset.Height) <= 720 ? 720 : 1080;
            }
            ExportAspectRatioCombo.SelectedIndex = VideoOutputFormats.AspectRatios.ToList().IndexOf(ratio);
            ExportResolutionCombo.SelectedIndex = resolution == 720 ? 1 : 0;
            ExportFrameRateCombo.SelectedIndex = preset.FramesPerSecond switch { 24 => 0, 60 => 2, _ => 1 };
            InputOffsetText.Text = _workspace.Project.InputOffsetMilliseconds.ToString(CultureInfo.CurrentCulture);
        }
        finally { _syncingExportFormat = false; }
    }

    private void CancelOperationButton_Click(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();
}
