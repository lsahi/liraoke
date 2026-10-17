using System.ComponentModel;
using System.IO;
using System.Windows;
using KaraokeFilmMaker.App.Dialogs;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private bool _allowClose;
    private bool _closingRequested;
    private readonly Func<Window, string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult, MessageBoxResult>
        _closePrompt = ShowDefaultClosePrompt;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _workspace is null) { return; }
        e.Cancel = true;
        if (_closingRequested) { return; }
        _closingRequested = true;
        IsEnabled = false;
        _recoveryTimer.Stop();
        // Always leave WPF's Closing event before prompting or calling Close again.
        // An async method can otherwise run synchronously when there is nothing to save.
        Dispatcher.BeginInvoke(new Action(async () => await CompleteCloseAsync()));
    }

    private async Task CompleteCloseAsync()
    {
        try
        {
            _operationCancellation?.Cancel();
            while (_isBusy || _savingRecovery) { await Task.Delay(50); }
            if (_workspace is null) { _allowClose = true; Close(); return; }
            if ((_markers.Count > 0 || _undoneMarkers.Count > 0) && ShowClosePrompt(
                $"还有 {_markers.Count} 个未应用打点、{_undoneMarkers.Count} 个可重做打点，只在内存中。\n建议返回打轴，完成当前句后按 Enter 应用并保存。\n仍要放弃这些打点并关闭吗？",
                "尚未应用的打点", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { return; }
            ApplyLyricRowsToProject(); ApplyVideoRowsToProject(); ApplySubtitleStyleFields(); ApplyExportPreset();
            bool quickEditPending = HasQuickEditDraft();
            bool sourceEditPending = HasSourceLyricDraft();
            if (quickEditPending || sourceEditPending || ProjectRecoveryStore.Content(_workspace.Project) != _lastExplicitContent)
            {
                MessageBoxResult choice = ShowClosePrompt($"“{_workspace.Project.Title}”有尚未保存的更改。\n保存：保存项目后关闭。\n不保存：不更新项目文件并关闭（已应用编辑仍保留独立恢复点）。\n取消：返回继续编辑。"
                    + (quickEditPending ? "\n快速编辑框中的文字 / 读音草稿也将随“保存”一起保存。" : "")
                    + (sourceEditPending ? "\n歌词获取 / 预览页正在修改的歌词草稿也将随“保存”一起保存；选择“不保存”会放弃此草稿。" : ""),
                    "关闭工作区", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
                if (choice is not (MessageBoxResult.Yes or MessageBoxResult.No)) { return; }
                if (choice == MessageBoxResult.Yes)
                {
                    var beforeDraft = _workspace;
                    try
                    {
                        if (quickEditPending) { ApplyQuickEditDraftForClose(); }
                        if (sourceEditPending && !await ApplySourceLyricDraftAsync()) { return; }
                    }
                    finally
                    {
                        // Applying a draft may succeed even if the following disk save fails.
                        // Keep that applied edit undoable when the user returns to the window.
                        RecordHistory(beforeDraft);
                    }
                    _workspace = await _workspaceService.SaveAsync(_workspace);
                    _lastExplicitContent = ProjectRecoveryStore.Content(_workspace.Project);
                }
                else { await _recoveryStore.SaveAsync(_workspace.RootPath, _workspace.Project); }
            }
            _allowClose = true;
            Close();
        }
        catch (Exception exception)
        {
            _allowClose = false;
            await LogErrorAsync(exception);
            if (ShowClosePrompt($"无法保存当前编辑：{exception.Message}\n是否仍然关闭？", "关闭工作区",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            {
                _allowClose = true; Close();
            }
        }
        finally
        {
            _closingRequested = false;
            if (!_allowClose) { IsEnabled = true; _recoveryTimer.Start(); }
        }
    }

    private MessageBoxResult ShowClosePrompt(string message, string title,
        MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult) =>
        _closePrompt(this, message, title, buttons, image, defaultResult);

    private static MessageBoxResult ShowDefaultClosePrompt(Window owner, string message, string title,
        MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        if (buttons != MessageBoxButton.YesNoCancel)
        { return MessageBox.Show(owner, message, title, buttons, image, defaultResult); }
        var dialog = new SaveChangesWindow(message) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Choice;
    }

    private void ApplyQuickEditDraftForClose()
    {
        if (_workspace is null || _recordingEditTarget is not { } target) { return; }
        var lines = _workspace.Project.Lyrics.Lines.ToArray();
        int index = Array.FindIndex(lines, line => line.Id == target.LineId);
        if (index < 0) { return; }
        lines[index] = LyricUnitEditor.Edit(lines[index], target.UnitId, QuickEditText.Text,
            QuickEditReading.Text, _workspace.Project.Lyrics.Language);
        lines[index] = _languageAnalysis.RefreshPresentation(lines[index]);
        _workspace = _workspace with { Project = _workspace.Project with { Lyrics = _workspace.Project.Lyrics with { Lines = lines } } };
        // A failed disk write may return to editing; keep the row model in sync with the draft.
        RefreshLyricRows();
    }

    private async Task LogErrorAsync(Exception exception)
    {
        if (_workspace is null) { return; }
        try
        {
            string directory = Path.Combine(_workspace.RootPath, "logs");
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(Path.Combine(directory, "application.log"), $"{DateTimeOffset.UtcNow:O}\n{exception}\n\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
