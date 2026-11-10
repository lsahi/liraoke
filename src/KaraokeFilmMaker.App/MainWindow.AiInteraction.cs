using System.IO;
using System.Windows;
using System.Windows.Controls;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.App;
public partial class MainWindow
{
    private bool _aiTaskRunning, _addingAiAsset;
    private TaskCompletionSource? _assetImportFinished;
    private CancellationTokenSource? _assetImportCancellation;
    private WorkspaceSession? _aiBaseline;
    private readonly SemaphoreSlim _interactivePlayback=new(1);
    private async Task RunAiOperationAsync(Func<Task> operation)
    {
        if(_isBusy)return;
        if(AssistantPanel.Visibility!=Visibility.Visible)_aiInspectorWasEnabled=PreviewSettingsPanel.IsEnabled;
        _aiTaskRunning=true;_aiBaseline=_workspace;
        UIElement[] locked=[(UIElement)RecordingTab.Content,(UIElement)LyricSourceTab.Content,(UIElement)SubtitleStyleTab.Content,PreviewSettingsPanel,RemoveMediaAssetButton,UndoProjectButton,RecoveryButton,SearchMusicButton];
        var states=locked.Select(e=>e.IsEnabled).ToArray();
        foreach(var e in locked)e.IsEnabled=false;
        ProjectTimeline.AllowEdits=false;
        try { await RunUiOperationAsync(async()=>{try{await operation();}finally{if(_assetImportFinished is {} pending)await pending.Task;}}); }
        finally
        {
            _aiTaskRunning=false;_aiBaseline=null;ProjectTimeline.AllowEdits=true;
            for(int i=0;i<locked.Length;i++)locked[i].IsEnabled=states[i];
            PreviewSettingsPanel.IsEnabled=AssistantPanel.Visibility!=Visibility.Visible && _aiInspectorWasEnabled;
        }
    }
    private void EnableAiReadAndAdd()
    {
        EditorTabs.IsEnabled=true;
        PlayPauseButton.IsEnabled=_playback.IsLoaded;PositionSlider.IsEnabled=_playback.IsLoaded;
        PlaybackRateCombo.IsEnabled=true;ProjectTimeline.IsEnabled=true;
        ImportImageButton.IsEnabled=_workspace is not null;OpenAudioButton.IsEnabled=ImportVideoButton.IsEnabled=_workspace is not null;
        CancelOperationButton.ToolTip="取消正在执行的 AI 任务";
    }
    private async Task RunPlaybackOperationAsync(Func<Task> operation)
    {
        if(!_aiTaskRunning){await RunUiOperationAsync(operation);return;}
        await _interactivePlayback.WaitAsync();
        try {await operation();}
        catch(Exception e)when(e is InvalidOperationException or IOException or OperationCanceledException){StatusText.Text=e.Message;}
        finally{_interactivePlayback.Release();}
    }
    private async Task AddLibraryAssetDuringAiAsync(string path,MediaKind kind)
    {
        if(_workspace is null||_addingAiAsset)return;
        _addingAiAsset=true;_assetImportCancellation=new();_assetImportFinished=new(TaskCreationOptions.RunContinuationsAsynchronously);var session=_workspace;var baseline=_aiBaseline??session;
        try
        {
            // Import into a detached project snapshot, then merge ONLY the newly created asset.
            // Never replace the current song, timeline, lyrics or partial AI results.
            var imported=await _workspaceService.ImportMediaAsync(session,path,kind,_assetImportCancellation.Token);
            var added=imported.Project.MediaAssets.Where(a=>!session.Project.MediaAssets.Any(old=>old.Id==a.Id)).ToArray();
            if(_workspace?.Project.Id!=session.Project.Id)throw new InvalidOperationException("项目已切换，新增素材未合并。");
            var before=_workspace with {Project=baseline.Project with {MediaAssets=_workspace.Project.MediaAssets}};
            _workspace=_workspace with {Project=_workspace.Project with {MediaAssets=[.._workspace.Project.MediaAssets,..added]}};
            RecordHistory(before);
            foreach(var asset in added.Where(a=>a.Kind is MediaKind.Video or MediaKind.Image))
            {
                var files=asset.Kind==MediaKind.Image
                    ?await new KaraokeFilmMaker.Infrastructure.Media.ImagePreviewCache(_mediaTools).PrepareAsync(session.RootPath,asset,_assetImportCancellation.Token)
                    :await new KaraokeFilmMaker.Infrastructure.Media.VideoPreviewCache(_mediaTools).PrepareAsync(session.RootPath,asset,_assetImportCancellation.Token);
                _previewPaths[asset.Id]=files.ProxyPath;_thumbnailPaths[asset.Id]=files.ThumbnailPath;
            }
            RefreshMediaLibrary();RefreshWorkspaceDisplay();
            StatusText.Text="已添加到素材库；当前歌曲和时间轴保持不变。任务完成后可使用新素材。";
        }
        catch(Exception e)when(e is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException){StatusText.Text=e.Message;}
        finally{_addingAiAsset=false;_assetImportCancellation?.Dispose();_assetImportCancellation=null;_assetImportFinished?.TrySetResult();}
    }
}
