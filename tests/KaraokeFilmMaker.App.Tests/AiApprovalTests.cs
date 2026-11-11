using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Music;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Infrastructure.Music;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Tests;
public sealed class AiApprovalTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static object? Get(MainWindow w,string name) => typeof(MainWindow).GetField(name,Private)!.GetValue(w);
    private static void Set(MainWindow w,string name,object? value) => typeof(MainWindow).GetField(name,Private)!.SetValue(w,value);
    private static Task<object> Call(MainWindow w,Guid? id,string name,string args,CancellationToken token=default) =>
        (Task<object>)typeof(MainWindow).GetMethod("ExecuteAiToolAsync",Private)!.Invoke(w,[id,name,JsonDocument.Parse(args).RootElement.Clone(),token])!;
    private static void Decide(MainWindow w,bool approve)
    {
        var panel=(AiChatPanel)w.FindName("AssistantPanel"); var messages=(StackPanel)panel.FindName("Messages");
        var card=(StackPanel)((Border)messages.Children[^1]).Child;
        var buttons=(WrapPanel)card.Children[2];
        ((Button)buttons.Children[approve?0:1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static Task Run(Func<MainWindow,string,Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{
            var dispatcher=Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => {
                string root=Path.Combine(Path.GetTempPath(),"LiraokeApprovalTests",Guid.NewGuid().ToString("N"));
                MainWindow? w=null;
                try { Directory.CreateDirectory(root); w=new MainWindow(new UserPreferenceStore(root)); await action(w,root); completion.TrySetResult(); }
                catch(Exception e){completion.TrySetException(e);}
                finally { if(w is not null){Set(w,"_workspace",null);Set(w,"_allowClose",true);w.Close();} if(Directory.Exists(root))Directory.Delete(root,true); dispatcher.InvokeShutdown(); }
            }); Dispatcher.Run();
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task.WaitAsync(TimeSpan.FromSeconds(50));
    }
    [Fact] public Task AlignmentGuideAndRejectedInstallWorkWithoutAProject() => Run(async(w,root)=>{
        string pluginRoot=Path.Combine(root,"plugins");
        var service=new KaraokeFilmMaker.Infrastructure.Alignment.LocalAlignmentService(pluginRoot);
        Set(w,"_alignment",service);
        var guide=JsonSerializer.SerializeToElement(await Call(w,null,"get_alignment_install_guide","{}"));
        Assert.Contains("install_alignment_plugin",guide.GetProperty("skill").GetString());
        var status=JsonSerializer.SerializeToElement(await Call(w,null,"get_alignment_plugins","{}"));
        Assert.Equal(2,status.GetProperty("plugins").GetArrayLength());
        Assert.False(Directory.Exists(pluginRoot));
        var pending=Call(w,null,"install_alignment_plugin","{\"pluginId\":\"nextfire\",\"device\":\"cpu\"}");
        if(service.Status()[0].CanInstallCpu)
        {
            Assert.False(pending.IsCompleted);Decide(w,false);
            Assert.False(JsonSerializer.SerializeToElement(await pending).GetProperty("approved").GetBoolean());
        }
        else Assert.False(JsonSerializer.SerializeToElement(await pending).GetProperty("installed").GetBoolean());
        Assert.False(Directory.Exists(pluginRoot));Assert.Null(Get(w,"_workspace"));
    });
    [Fact] public Task ApprovalIsRequiredAndSavesRealProject() => Run(async(w,root)=>{
        var p=KaraokeProject.Create("Approval",DateTimeOffset.UtcNow); string file=Path.Combine(root,"Approval.lrn");
        Set(w,"_workspace",new WorkspaceSession(root,p,file));
        var pending=Call(w,p.Id,"update_subtitle_style","{\"patch\":{\"primaryFontSize\":82}}");
        Assert.False(pending.IsCompleted);Assert.False(File.Exists(file));Assert.Equal(p,((WorkspaceSession)Get(w,"_workspace")!).Project);
        Decide(w,true); var outcome=JsonSerializer.SerializeToElement(await pending);
        Assert.True(outcome.GetProperty("saved").GetBoolean());
        Assert.Equal(82,(await new JsonProjectRepository().LoadAsync(file)).SubtitleStyle.PrimaryFontSize);
        Assert.Single((Stack<KaraokeProject>)Get(w,"_undoProjects")!);
        Assert.Equal(ProjectRecoveryStore.Content(((WorkspaceSession)Get(w,"_workspace")!).Project),Get(w,"_lastExplicitContent"));
    });
    [Fact] public Task RejectCancelAndStaleApprovalNeverWrite() => Run(async(w,root)=>{
        var p=KaraokeProject.Create("Reject",DateTimeOffset.UtcNow); var session=new WorkspaceSession(root,p,Path.Combine(root,"Reject.lrn")); Set(w,"_workspace",session);
        var rejected=Call(w,p.Id,"update_song_title","{\"patch\":{\"text\":\"Changed\"}}"); Decide(w,false);
        Assert.False(JsonSerializer.SerializeToElement(await rejected).GetProperty("executed").GetBoolean());Assert.Equal(session,Get(w,"_workspace"));
        using var cancel=new CancellationTokenSource();var stopped=Call(w,p.Id,"update_subtitle_style","{\"patch\":{\"primaryFontSize\":90}}",cancel.Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>stopped);Assert.Equal(session,Get(w,"_workspace"));
        var stale=Call(w,p.Id,"update_subtitle_style","{\"patch\":{\"primaryFontSize\":90}}");Set(w,"_workspace",session with {Project=p with {Title="Manual edit"}});Decide(w,true);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>stale);Assert.False(File.Exists(session.ProjectFilePath));
    });
    [Fact] public Task SaveFailureRetainsUndoAndDoesNotClaimSaved() => Run(async(w,root)=>{
        var p=KaraokeProject.Create("Failure",DateTimeOffset.UtcNow);string blocker=Path.Combine(root,"blocked");File.WriteAllText(blocker,"not a directory");string file=Path.Combine(blocker,"blocked.lrn");
        Set(w,"_workspace",new WorkspaceSession(root,p,file));
        var pending=Call(w,p.Id,"update_subtitle_style","{\"patch\":{\"primaryFontSize\":84}}");Decide(w,true);
        var result=JsonSerializer.SerializeToElement(await pending);Assert.False(result.GetProperty("saved").GetBoolean());Assert.True(result.GetProperty("executed").GetBoolean());
        Assert.Equal(84,((WorkspaceSession)Get(w,"_workspace")!).Project.SubtitleStyle.PrimaryFontSize);Assert.Single((Stack<KaraokeProject>)Get(w,"_undoProjects")!);Assert.Null(Get(w,"_lastExplicitContent"));
    });
    [Fact] public Task CreateWorkspacePersistsExistingProjectAndNewEntry() => Run(async(w,root)=>{
        var p=KaraokeProject.Create("Existing",DateTimeOffset.UtcNow);string oldFile=Path.Combine(root,"existing.lrn");Set(w,"_workspace",new WorkspaceSession(root,p,oldFile));
        var task=(Task<object>)typeof(MainWindow).GetMethod("CreateAiWorkspaceAsync",Private)!.Invoke(w,[Path.Combine(root,"new"),"新歌曲",CancellationToken.None])!;
        await task;var current=(WorkspaceSession)Get(w,"_workspace")!;Assert.NotEqual(p.Id,current.Project.Id);Assert.True(File.Exists(oldFile));Assert.True(File.Exists(current.ProjectFilePath));
        Assert.Equal("新歌曲",(await new JsonProjectRepository().LoadAsync(current.ProjectFilePath!)).Title);
    });
    [Theory][InlineData(true)][InlineData(false)]
    public Task LyricsImportRequiresApprovalAndSavesUndo(bool approve)=>Run(async(w,root)=>{
        var p=KaraokeProject.Create("Lyrics",DateTimeOffset.UtcNow);string file=Path.Combine(root,"Lyrics.lrn");
        Set(w,"_workspace",new WorkspaceSession(root,p,file));
        var panel=(AiChatPanel)w.FindName("AssistantPanel");var messages=(StackPanel)panel.FindName("Messages");int before=messages.Children.Count;
        var pending=Call(w,p.Id,"import_lyrics","{\"format\":\"txt\",\"text\":\"hello world\"}");
        for(int i=0;i<200 && messages.Children.Count==before && !pending.IsCompleted;i++)await Task.Delay(10);
        Assert.False(pending.IsCompleted);Assert.Equal(p,((WorkspaceSession)Get(w,"_workspace")!).Project);Assert.False(File.Exists(file));
        Decide(w,approve);var response=JsonSerializer.SerializeToElement(await pending);
        Assert.Equal(approve,response.GetProperty("approved").GetBoolean());
        if(approve){Assert.True(response.GetProperty("saved").GetBoolean());Assert.Equal("hello world",Assert.Single((await new JsonProjectRepository().LoadAsync(file)).Lyrics.Lines).PrimaryText);Assert.Single((Stack<KaraokeProject>)Get(w,"_undoProjects")!);}
        else Assert.False(File.Exists(file));
    });
    private static void Snapshot(MainWindow w,string name)
    {
        string? directory=Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");if(directory is null)return;
        Directory.CreateDirectory(directory);var content=(FrameworkElement)w.Content;var bitmap=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(directory,name));encoder.Save(file);
    }
    [Fact] public Task InlinePlayerDecodesLocalWavAndStops() => Run(async(w,root)=>{
        string dir=Path.Combine(root,"player");Directory.CreateDirectory(dir);string path=Path.Combine(dir,"audio.wav");
        using(var writer=new BinaryWriter(File.Create(path)))
        {
            int bytes=48000*2*2*4;writer.Write("RIFF"u8);writer.Write(bytes+36);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)2);writer.Write(48000);writer.Write(192000);writer.Write((short)4);writer.Write((short)16);writer.Write("data"u8);writer.Write(bytes);writer.Write(new byte[bytes]);
        }
        var chat=(AiChatPanel)w.FindName("AssistantPanel");w.Show();((Button)w.FindName("AiOpenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool paused=false;chat.ShowMusicCard(new PreparedMusicAudio(dir,path,new MusicTrack("netease","local","Local WAV","Test","",4),"PCM",4,true,"完整音频"),()=>{paused=true;return Task.CompletedTask;},()=>Task.CompletedTask,()=>{});
        var card=(StackPanel)((Border)((StackPanel)chat.FindName("Messages")).Children[^1]).Child;
        var player=(MediaElement)card.Children[^1];player.Volume=0;
        var row=(WrapPanel)card.Children[2];((Button)row.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        DateTime deadline=DateTime.UtcNow.AddSeconds(12);
        while(!player.NaturalDuration.HasTimeSpan || player.Position.TotalSeconds < .05){Assert.True(DateTime.UtcNow<deadline,"Local WAV preview did not start.");await Task.Delay(40);}
        Assert.True(paused);Assert.InRange(player.NaturalDuration.TimeSpan.TotalSeconds,3.9,4.1);
        ((Button)row.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal("▶ 播放",((Button)row.Children[0]).Content);
        chat.ClearInlineContent();Assert.False(Directory.Exists(dir));
    });
    [Fact] public Task InlineCardsRenderAndReleaseAudioOnClear() => Run(async(w,root)=>{
        var chat=(AiChatPanel)w.FindName("AssistantPanel");w.Show();((Button)w.FindName("AiOpenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        string temp=Path.Combine(root,"audio");Directory.CreateDirectory(temp);bool released=false;
        var audio=new PreparedMusicAudio(temp,Path.Combine(temp,"preview.wav"),new MusicTrack("netease","test","おかえりなさい","测试歌手","",123),"标准音质",123,true,"已校验完整时长，可试听或导入");
        chat.ShowMusicCard(audio,()=>Task.CompletedTask,()=>Task.CompletedTask,()=>released=true);
        w.Width=1100;w.Height=800;w.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);w.UpdateLayout();
        Snapshot(w,"ai-inline-music.png");
        var approval=chat.RequestApprovalAsync("修改字幕样式","字号：64 → 82\n批准后自动保存整个项目。",default);
        w.Width=1100;w.Height=800;w.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);w.UpdateLayout();
        string? directory=Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if(directory is not null){Directory.CreateDirectory(directory);var content=(FrameworkElement)w.Content;var bitmap=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(directory,"ai-inline-approval-music.png"));png.Save(file);}
        chat.ClearInlineContent();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>approval);Assert.True(released);Assert.False(Directory.Exists(temp));
    });
}
