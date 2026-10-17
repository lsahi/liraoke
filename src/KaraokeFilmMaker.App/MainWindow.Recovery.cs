using System.IO;
using System.Windows;
using System.Windows.Threading;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private readonly ProjectRecoveryStore _recoveryStore = new();
    private readonly DispatcherTimer _recoveryTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private string? _lastExplicitContent;
    private string? _lastRecoveryContent;
    private KaraokeProject? _availableRecovery;
    private bool _savingRecovery;

    private async Task InitializeRecoveryAsync()
    {
        if (_workspace is null) { return; }
        _lastExplicitContent = ProjectRecoveryStore.Content(_workspace.Project);
        _lastRecoveryContent = null;
        try { _availableRecovery = await _recoveryStore.FindAsync(_workspace.RootPath, _workspace.Project); }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            _availableRecovery = null;
            StatusText.Text = $"恢复文件不可用，已打开明确保存的项目：{exception.Message}";
        }
        RecoveryButton.Visibility = _availableRecovery is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void RecoveryTimer_Tick(object? sender, EventArgs e)
    {
        if (_workspace is null || _isBusy || _savingRecovery || _isClosing || _availableRecovery is not null
            || (IsRecordingMode && _playback.IsPlaying)) { return; }
        _savingRecovery = true;
        var before = _workspace;
        try
        {
            ApplyLyricRowsToProject();
            ApplyVideoRowsToProject();
            ApplySubtitleStyleFields();
            ApplyExportPreset();
            RecordHistory(before);
            string content = ProjectRecoveryStore.Content(_workspace.Project);
            if (content != _lastExplicitContent && content != _lastRecoveryContent)
            {
                var snapshot = _workspace;
                await _recoveryStore.SaveAsync(snapshot.RootPath, snapshot.Project);
                if (_workspace?.Project.Id == snapshot.Project.Id) { _lastRecoveryContent = content; }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
        {
            StatusText.Text = $"自动恢复点暂未更新：{exception.Message}";
        }
        finally { _savingRecovery = false; }
    }

    private async void RecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || _availableRecovery is not { } recovery) { return; }
        await RunUiOperationAsync(async () =>
        {
            _workspace = _workspace with { Project = recovery };
            _availableRecovery = null;
            RecoveryButton.Visibility = Visibility.Collapsed;
            RefreshLyricRows(); RefreshVideoRows(); RefreshSubtitleStyleFields(); RefreshExportPreset();
            await ResetPlaybackAsync();
            await PreparePreviewsAsync();
            RefreshVideoRows();
            await LoadMasterAudioIfAvailableAsync();
            StatusText.Text = "已恢复未保存编辑；原来的明确保存文件仍保留，点击保存可确认恢复结果。";
        });
    }
}
