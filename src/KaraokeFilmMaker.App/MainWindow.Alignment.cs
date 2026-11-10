using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using KaraokeFilmMaker.App.Dialogs;
using KaraokeFilmMaker.Application.Alignment;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Infrastructure.Alignment;

namespace KaraokeFilmMaker.App;
public partial class MainWindow
{
    private readonly LocalAlignmentService _alignment = new();
    private void AlignmentPlugins_Click(object sender,RoutedEventArgs e)
    {
        if(_isBusy)return;
        new AlignmentPluginsWindow(_alignment){Owner=this}.ShowDialog();
    }
    private async void AlignLyrics_Click(object sender,RoutedEventArgs e)
    {
        try { await StartAlignmentAsync(false,SelectedAlignmentPlugin(),AlignmentCorrectReadings.IsChecked==true,CancellationToken.None); }
        catch(Exception error){AlignmentStatus.Text=error is OperationCanceledException?"打轴已取消。":error.Message;}
    }
    private string SelectedAlignmentPlugin() => (AlignmentBackend.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"auto";
    private async Task<object> ExecuteAlignmentToolAsync(string name,JsonElement args,CancellationToken token)
    {
        if(name=="get_alignment_install_guide")return new{skill=LocalAlignmentService.InstallGuide};
        if(name=="get_alignment_plugins")return new{plugins=_alignment.Status(),preferred=_workspace is null?null:AlignmentPlugins.Preferred(_workspace.Project.Lyrics),hardware=_alignment.Hardware()};
        if(_isBusy)throw new InvalidOperationException("请等待当前任务完成。");
        if(name=="open_alignment_plugins") {new AlignmentPluginsWindow(_alignment){Owner=this}.ShowDialog();return new{plugins=_alignment.Status()};}
        if(name=="align_lyrics")return await StartAlignmentAsync(true,TextArg(args,"pluginId")??"auto",!args.TryGetProperty("correctReadings",out var c)||c.GetBoolean(),token);
        string id=TextArg(args,"pluginId")??"",device=TextArg(args,"device")??"cpu";var plugin=AlignmentPlugins.Get(id);
        var state=_alignment.Status().Single(s=>s.Plugin.Id==id);
        if(!(device=="gpu"?state.CanInstallGpu:state.CanInstallCpu))return new{installed=false,reason=state.Message};
        if(_aiActionPending)throw new InvalidOperationException("请先完成当前待批准操作。");
        _aiActionPending=true;
        try
        {
            if(!await AssistantPanel.RequestApprovalAsync("安装本地打轴插件",$"{plugin.Name} · {device}\n{plugin.License}\n从官方来源下载模型与运行环境（数GB），安装后执行真实推理自检。\n{state.Message}",token))return new{approved=false,installed=false};
            var progress=AssistantPanel.CreateToolProgress("准备安装插件…");bool installed=false;string? error=null;
            await RunAiOperationAsync(async()=>
            {
                using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,_operationCancellation!.Token);
                try {await _alignment.InstallAsync(id,device,progress,linked.Token);installed=true;}
                catch(OperationCanceledException){error="安装已取消，旧安装保留。";}
                catch(Exception e){error=e.Message;}
            });
            AssistantPanel.ReportActionResult(installed?"插件安装完成并通过推理自检。":error??"安装未完成。");
            return new{approved=true,installed,error};
        }
        finally{_aiActionPending=false;}
    }
    private async Task<object> StartAlignmentAsync(bool fromAgent,string selected,bool correctReadings,CancellationToken token)
    {
        if(_workspace is null||_isBusy)throw new InvalidOperationException("请先打开项目并等待当前任务完成。");
        CheckAiDrafts();
        if(_workspace.Project.Lyrics.Lines.All(l=>l.Units.Count==0||AlignmentEdits.Protected(l)))return new{applied=0,message="没有待自动打轴单元；已有人工打点保留。"};
        string id=selected=="auto"?AlignmentPlugins.Preferred(_workspace.Project.Lyrics):selected;var plugin=AlignmentPlugins.Get(id);
        if(!_alignment.Status().Single(s=>s.Plugin.Id==id).Installed)throw new InvalidOperationException($"请先安装 {plugin.Name}：点击“打轴插件…”或让 AI 安装。不会自动切换到其他模型。");
        bool needsReadings=_workspace.Project.Lyrics.Lines.Any(l=>!AlignmentEdits.Protected(l)&&ReadingCorrection.IsJapanese(l,_workspace.Project.Lyrics.Language)&&l.ReadingSource is not (KaraokeFilmMaker.Domain.Lyrics.ReadingSource.Manual or KaraokeFilmMaker.Domain.Lyrics.ReadingSource.Confirmed)&&l.AiReadingReview?.Status is not ("applied" or "confirmed"));
        if(correctReadings&&needsReadings)
        {
            var correction=await StartReadingCorrectionAsync(fromAgent,token);
            var state=JsonSerializer.SerializeToElement(correction);
            if(!state.TryGetProperty("completed",out var complete)||!complete.GetBoolean())return new{applied=0,message="发音校正尚未全部完成，请处理失败句或确认现有读音后再打轴。",correction};
        }
        var project=_workspace.Project;var asset=project.MediaAssets.FirstOrDefault(a=>a.Id==project.MasterAudioAssetId)??throw new InvalidOperationException("请先导入歌曲。");
        string snapshot=ProjectContent(project);
        string detail=$"使用 {plugin.Name}，先用 Demucs 提取人声，再定位句段、裁切人声逐句对齐（会增加处理时间）。保留人工打点；结果作为初稿，可撤销并自动保存。";
        if(fromAgent)
        {if(!await AssistantPanel.RequestApprovalAsync("本地自动打轴",detail,token))return new{approved=false,applied=0};}
        else if(MessageBox.Show(this,detail,"自动打轴",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return new{applied=0};
        CheckAiProject(project.Id);CheckAiDrafts();if(!AiProjectWritePolicy.OnlyAddedMedia(project,_workspace!.Project))throw new InvalidOperationException("项目已变化，请重新发起打轴。");
        var previous=_workspace;int applied=0;bool saved=false;string? error=null;string[] skipped=[];string? log=null;
        var chatProgress=fromAgent?AssistantPanel.CreateToolProgress("开始本地打轴…"):null;
        await RunAiOperationAsync(async()=>
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,_operationCancellation!.Token);
            try
            {
                if(_playback.IsPlaying)await _playback.PauseAsync();
                string audio=Path.GetFullPath(Path.Combine(previous.RootPath,asset.RelativePath));
                string? lrc=project.Lyrics.Source is {} source?Path.GetFullPath(Path.Combine(previous.RootPath,source.ImportedFileRelativePath)):null;
                if(lrc is not null && (!lrc.StartsWith(Path.GetFullPath(previous.RootPath)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!File.Exists(lrc)))lrc=null;
                var progress=new Progress<string>(m=>{AlignmentStatus.Text=m;StatusText.Text=m;chatProgress?.Report(m);});
                var outcome=await _alignment.AlignAsync(id,audio,lrc,project.Lyrics,_mediaTools,progress,linked.Token);log=outcome.LogPath;
                (applied,saved,skipped,error)=await ApplyAlignmentOutcomeAsync(project,outcome,asset.Duration.TotalSeconds,linked.Token);
            }
            catch(OperationCanceledException){error="打轴已取消，未应用本轮结果。";}
            catch(Exception e){error=e.Message;}
            AlignmentStatus.Text=$"已应用 {applied} 句；跳过 {skipped.Length} 句。"+(saved?"已保存，可撤销。":"")+"\n"+error+"\n"+string.Join("；",skipped.Take(5))+(log is null?"":"\n日志："+log);
            StatusText.Text=AlignmentStatus.Text;chatProgress?.Report(AlignmentStatus.Text);
        });
        return new{applied,saved,skipped,error,log};
    }
    private async Task<(int Applied,bool Saved,string[] Skipped,string? Error)> ApplyAlignmentOutcomeAsync(
        KaraokeFilmMaker.Domain.Projects.KaraokeProject project,AlignmentOutcome outcome,double duration,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // We still own the busy scope. Only new operations require an idle client.
        CheckAiProjectIdentity(project.Id);
        if (!_aiTaskRunning || !_isBusy || _aiBaseline?.Project.Id != project.Id)
            throw new InvalidOperationException("打轴任务已失去执行状态，未应用结果。");
        if(!AiProjectWritePolicy.OnlyAddedMedia(project,_workspace!.Project))
            throw new InvalidOperationException("项目在处理期间已变化，未应用结果。");
        var edits=AlignmentEdits.Apply(project.Lyrics,outcome,duration);
        bool saved=false;string? error=null;
        if(edits.Applied>0)
        {
            CommitRecordingLines(edits.Document.Lines,_browsedRecordingLineId);
            try{await SaveAiWorkspaceAsync();saved=true;}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException)
            {error="已应用，但自动保存失败，请手动保存。";}
        }
        return (edits.Applied,saved,edits.Skipped.ToArray(),error);
    }

}
