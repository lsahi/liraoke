using System.IO;
using KaraokeFilmMaker.App.Dialogs;
using KaraokeFilmMaker.Infrastructure.Workspaces;
using Microsoft.Win32;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private async Task ShowOpenWorkspaceDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开 Liraoke 项目",
            Filter = "Liraoke 项目 (*.lrn;project.kfp)|*.lrn;project.kfp|Liraoke 项目 (*.lrn)|*.lrn|旧项目 (project.kfp)|project.kfp",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (_dialogLocations.ShowDialog(dialog, DialogLocation.OpenWorkspace, this))
        { await OpenWorkspaceEntryAsync(dialog.FileName); }
    }

    internal async Task OpenWorkspaceEntryAsync(string fileOrDirectory)
    {
        await RunUiOperationAsync(async () =>
        {
            _workspace = await _workspaceService.OpenAsync(fileOrDirectory, _operationCancellation!.Token);
            _dialogLocations.Remember(DialogLocation.OpenWorkspace, _workspace.RootPath);
            ResetMarkers();
            RefreshMarkerList();
            RefreshWorkspaceDisplay();
            RefreshLyricRows();
            RefreshVideoRows();
            RefreshSubtitleStyleFields();
            await ResetPlaybackAsync();
            await PreparePreviewsAsync();
            RefreshVideoRows();
            RefreshMediaLibrary();
            await LoadMasterAudioIfAvailableAsync();
            RefreshExportPreset();
            await InitializeRecoveryAsync();
            StatusText.Text = $"项目已打开：{Path.GetFileName(_workspace.ProjectFilePath)}";
        });
    }
}

/// <summary>Command-line entry validation is independent of WPF startup and OS file associations.</summary>
public static class WorkspaceStartupArguments
{
    public static string? Resolve(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0) { return null; }
        if (arguments.Count != 1 || !WorkspaceProjectPath.IsProjectFile(arguments[0]))
        { throw new ArgumentException("请提供一个 .lrn 项目文件。例如：Liraoke.exe \"D:\\歌曲工作区\\歌曲.lrn\""); }
        string path = Path.GetFullPath(arguments[0]);
        if (!File.Exists(path)) { throw new FileNotFoundException("找不到要打开的项目文件。", path); }
        return path;
    }
}
