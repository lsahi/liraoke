using System.Text.Json;
using System.Windows;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Infrastructure.Audio;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private readonly Stack<KaraokeProject> _undoProjects = [];
    private readonly Stack<KaraokeProject> _redoProjects = [];
    private bool _restoringHistory;

    private void RecordHistory(WorkspaceSession? before)
    {
        if (_restoringHistory || _workspace is null)
        {
            return;
        }

        if (before is null || before.Project.Id != _workspace.Project.Id)
        {
            _undoProjects.Clear();
            _redoProjects.Clear();
            return;
        }

        if (ProjectContent(before.Project) != ProjectContent(_workspace.Project))
        {
            _undoProjects.Push(before.Project);
            _redoProjects.Clear();
        }
    }

    private static string ProjectContent(KaraokeProject project) =>
        JsonSerializer.Serialize(project with { ModifiedAtUtc = DateTimeOffset.MinValue });

    private async void UndoProjectButton_Click(object sender, RoutedEventArgs e) =>
        await RestoreHistoryAsync(redo: false);

    private async void RedoProjectButton_Click(object sender, RoutedEventArgs e) =>
        await RestoreHistoryAsync(redo: true);

    private async Task RestoreHistoryAsync(bool redo)
    {
        if (_workspace is null || _isBusy)
        {
            return;
        }

        _restoringHistory = true;
        try
        {
            await RunUiOperationAsync(async () =>
            {
                WorkspaceSession before = _workspace;
                ApplyLyricRowsToProject();
                ApplyVideoRowsToProject();
                ApplySubtitleStyleFields();
                ApplyExportPreset();
                if (ProjectContent(before.Project) != ProjectContent(_workspace.Project))
                {
                    _undoProjects.Push(before.Project);
                    _redoProjects.Clear();
                }

                Stack<KaraokeProject> source = redo ? _redoProjects : _undoProjects;
                Stack<KaraokeProject> destination = redo ? _undoProjects : _redoProjects;
                if (!source.TryPop(out KaraokeProject? restored))
                {
                    StatusText.Text = redo ? "没有可重做的项目编辑。" : "没有可撤销的项目编辑。";
                    return;
                }

                Guid? previousMaster = _workspace.Project.MasterAudioAssetId;
                destination.Push(_workspace.Project);
                _workspace = _workspace with { Project = restored with { ModifiedAtUtc = DateTimeOffset.UtcNow } };
                RefreshLyricRows();
                RefreshMarkerList(); ShowQuickEdit();
                RefreshVideoRows();
                RefreshSubtitleStyleFields();
                RefreshExportPreset();
                RefreshWorkspaceDisplay();
                if (previousMaster != restored.MasterAudioAssetId)
                {
                    await ResetPlaybackAsync();
                    await LoadMasterAudioIfAvailableAsync();
                }

                StatusText.Text = redo ? "已重做项目编辑。" : "已撤销项目编辑。";
            });
        }
        finally
        {
            _restoringHistory = false;
        }
    }

    private async Task ResetPlaybackAsync()
    {
        ClearVideoPreview();
        ClearAudioVisualization();
        _failedPreviewAssets.Clear();
        _loopRange = null;
        _countdownTarget = null;
        LoopLineButton.Content = "循环选中歌词行";
        await _playback.DisposeAsync();
        _playback = new ScaledAudioPlaybackSession();
        _preparedPlaybackKey = null;
        ProjectTimeline.Waveform = null;
        PositionSlider.Value = 0;
        PositionSlider.IsEnabled = false;
        PlayPauseButton.Content = "播放";
        ClockSourceText.Text = "时钟：等待歌曲";
    }
}
