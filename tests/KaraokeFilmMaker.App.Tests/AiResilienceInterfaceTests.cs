using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Application.Preferences;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Infrastructure.Ai;

namespace KaraokeFilmMaker.App.Tests;
public class AiResilienceInterfaceTests
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static object? Get(object target,string field)=>target.GetType().GetField(field,Hidden)!.GetValue(target);
    static void Set(object target,string field,object? value)=>target.GetType().GetField(field,Hidden)!.SetValue(target,value);
    static object? Call(object target,string method,params object?[] args)=>target.GetType().GetMethod(method,Hidden)!.Invoke(target,args);
    static Task Sta(Func<string,Task> action)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{
            var dispatcher=Dispatcher.CurrentDispatcher;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async()=>{
                string root=Path.Combine(Path.GetTempPath(),"LiraokeResilience",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
                try{await action(root);done.TrySetResult();}catch(Exception e){done.TrySetException(e);}finally{Directory.Delete(root,true);dispatcher.InvokeShutdown();}
            });Dispatcher.Run();
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
    [Fact]public Task FailedTurnKeepsPromptPartialAnswerAndToolResultsAcrossRestart()=>Sta(async root=>{
        var store=new UserPreferenceStore(root);var preset=AiModelCatalog.Presets[0];Guid id=Guid.NewGuid();
        store.SaveAiProvider(id,"test",true,preset.Endpoint,new("fake-key"),preset.Model,preset.ProviderId,preset.Protocol);
        var fake=new InterruptedChat();var state=new AiStateStore(Path.Combine(root,"ai-state"));var panel=new AiChatPanel();panel.Initialize(store,fake,state);
        var window=new Window{Content=panel,Width=390,Height=630};window.Show();
        try
        {
            ((TextBox)panel.FindName("MessageInput")).Text="安装我的模型";Call(panel,"Send");
            while(Get(panel,"_sending") is not null)await Task.Delay(10);
            var saved=state.LoadSession(id.ToString());Assert.False(saved.InFlight);
            Assert.Contains(saved.Messages,m=>m.Text=="安装我的模型");Assert.Contains(saved.Messages,m=>m.Text.Contains("installation-success-marker"));
            Assert.Contains(saved.Messages,m=>m.Text.Contains("部分回复"));Assert.DoesNotContain(saved.Messages,m=>m.Text.Contains("hidden-reasoning"));
            var resumed=new AiChatPanel();resumed.Initialize(store,fake,state);window.Content=resumed;
            ((TextBox)resumed.FindName("MessageInput")).Text="继续";Call(resumed,"Send");
            while(Get(resumed,"_sending") is not null)await Task.Delay(10);
            Assert.Contains(fake.History,m=>m.Text.Contains("installation-success-marker"));
            state.Remember("默认 GPU");Call(resumed,"NewChat_Click",resumed,new RoutedEventArgs());Assert.Single(state.Memories());Assert.Empty(state.LoadSession(id.ToString()).Messages);
            Snapshot(window,"ai-resilient-session.png");
        }
        finally{panel.Stop();window.Close();}
    });
    private sealed class InterruptedChat:IAiChatService
    {
        public IReadOnlyList<AiChatMessage> History=[];private int turns;
        public async IAsyncEnumerable<AiChatEvent> StreamAsync(AiProviderPreference p,AiCredentials c,IReadOnlyList<AiChatMessage> history,string prompt,[EnumeratorCancellation]CancellationToken token=default)
        {
            History=history;await Task.Yield();
            if(++turns>1){yield return new("text","继续处理");yield return new("done","");yield break;}
            yield return new("thinking","hidden-reasoning");yield return new("text","部分回复");
            yield return new("tool_result","installation-success-marker");throw new OperationCanceledException();
        }
    }
    [Fact]public Task AiTaskLocksEditsButAllowsPlaybackSeekAndAddOnlyMedia()=>Sta(async root=>{
        var w=new MainWindow(new UserPreferenceStore(root));var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var project=KaraokeProject.Create("Before",DateTimeOffset.UtcNow);Set(w,"_workspace",new WorkspaceSession(root,project));
            await ((IAudioPlaybackSession)Get(w,"_playback")!).DisposeAsync();var player=new MetronomeWorkflowTests.Playback();Set(w,"_playback",player);
            w.Show();
            var operation=(Task)Call(w,"RunAiOperationAsync",(Func<Task>)(async()=>{await gate.Task;var current=(WorkspaceSession)Get(w,"_workspace")!;Set(w,"_workspace",current with {Project=current.Project with {Title="AI edit"}});}))!;
            Assert.True((bool)Get(w,"_isBusy")!);Assert.True(((TabControl)w.FindName("EditorTabs")).IsEnabled);
            Assert.False(((Button)w.FindName("RemoveMediaAssetButton")).IsEnabled);Assert.False(((ProjectTimelineControl)w.FindName("ProjectTimeline")).AllowEdits);
            await (Task)Call(w,"TogglePlaybackAsync")!;Assert.True(player.IsPlaying);
            await (Task)Call(w,"RunPlaybackOperationAsync",(Func<Task>)(()=>player.SeekAsync(KaraokeFilmMaker.Domain.Time.ProjectTime.FromSeconds(7))))!;Assert.Equal(7,player.Position.TotalSeconds);
            await (Task)Call(w,"TogglePlaybackAsync")!;Assert.False(player.IsPlaying);
            byte[] pixels=new byte[]{0,0,255,255};var bitmap=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,pixels,4);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));string image=Path.Combine(root,"new.png");using(var file=File.Create(image))encoder.Save(file);
            await (Task)Call(w,"AddLibraryAssetDuringAiAsync",image,MediaKind.Image)!;
            var updated=(WorkspaceSession)Get(w,"_workspace")!;Assert.Single(updated.Project.MediaAssets);Assert.Null(updated.Project.MasterAudioAssetId);Assert.Empty(updated.Project.VideoClips);Assert.Equal(project.Lyrics,updated.Project.Lyrics);
            Snapshot(w,"ai-interactive-task.png");
            Call(w,"AiOpen_Click",w,new RoutedEventArgs());Call(w,"CollapseAiChat");
            Assert.False(((FrameworkElement)w.FindName("PreviewSettingsPanel")).IsEnabled);
            w.Width=1120;w.Height=740;Snapshot(w,"ai-interactive-compact.png");
            gate.SetResult();await operation;
            Assert.False((bool)Get(w,"_isBusy")!);Assert.True(((ProjectTimelineControl)w.FindName("ProjectTimeline")).AllowEdits);
            var undo=(Stack<KaraokeProject>)Get(w,"_undoProjects")!;Assert.Single(undo.Peek().MediaAssets);Assert.Equal("Before",undo.Peek().Title);
        }
        finally{gate.TrySetResult();Set(w,"_workspace",null);Set(w,"_allowClose",true);w.Close();}
    });
    [Fact] public Task ClosingWindowCancelsChatWithoutWorkspaceAndKeepsContext()=>Sta(async root=>{
        var preferences=new UserPreferenceStore(root);var preset=AiModelCatalog.Presets[0];var id=Guid.NewGuid();
        preferences.SaveAiProvider(id,"test",true,preset.Endpoint,new("fake-key"),preset.Model,preset.ProviderId,preset.Protocol);
        var state=new AiStateStore(Path.Combine(root,"state"));var window=new MainWindow(preferences,state);var fake=new WaitingChat();
        var panel=(AiChatPanel)window.FindName("AssistantPanel");panel.Initialize(preferences,fake,state);
        try
        {
            window.Show();((TextBox)panel.FindName("MessageInput")).Text="继续安装";Call(panel,"Send");await fake.Started.Task;
            var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);window.Closed+=(_,_)=>closed.TrySetResult();
            window.Close();await fake.Cancelled.Task;await closed.Task;
            while(Get(panel,"_sending") is not null)await Task.Delay(10);
            Assert.Contains(state.LoadSession(id.ToString()).Messages,m=>m.Text=="继续安装");
            Assert.False(state.LoadSession(id.ToString()).InFlight);
        }
        finally{panel.Stop();Set(window,"_allowClose",true);window.Close();}
    });
    private sealed class WaitingChat:IAiChatService
    {
        public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Cancelled=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<AiChatEvent> StreamAsync(AiProviderPreference p,AiCredentials c,IReadOnlyList<AiChatMessage> h,string prompt,[EnumeratorCancellation]CancellationToken token=default)
        {
            Started.TrySetResult();
            try{await Task.Delay(Timeout.Infinite,token);}finally{Cancelled.TrySetResult();}
            yield break;
        }
    }
    static void Snapshot(Window window,string name)
    {
        string? root=Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");if(root is null)return;Directory.CreateDirectory(root);window.UpdateLayout();
        var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(root,name));encoder.Save(file);
    }
}
