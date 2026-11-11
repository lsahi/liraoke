using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Alignment;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.App;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Tests;
public sealed class AlignmentResultCommitTests
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static object? Get(MainWindow w,string name)=>typeof(MainWindow).GetField(name,Hidden)!.GetValue(w);
    static void Set(MainWindow w,string name,object? value)=>typeof(MainWindow).GetField(name,Hidden)!.SetValue(w,value);
    static object? Call(MainWindow w,string name,params object?[] args)=>typeof(MainWindow).GetMethod(name,Hidden)!.Invoke(w,args);
    static Task<(int Applied,bool Saved,string[] Skipped,string? Error)> Apply(MainWindow w,KaraokeProject p,AlignmentOutcome outcome,CancellationToken token=default)
        =>(Task<(int,bool,string[],string?)>)Call(w,"ApplyAlignmentOutcomeAsync",p,outcome,200d,token)!;
    static async Task Owned(MainWindow window,Func<Task> action)
    {
        Exception? failure=null;
        await (Task)Call(window,"RunAiOperationAsync",(Func<Task>)(async()=>{try{await action();}catch(Exception e){failure=e;}}))!;
        if(failure is not null)ExceptionDispatchInfo.Capture(failure).Throw();
    }
    static Task Sta(Func<MainWindow,string,KaraokeProject,Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async()=>
            {
                string root=Path.Combine(Path.GetTempPath(),"alignment-commit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
                MainWindow? window=null;
                try
                {
                    window=new MainWindow(new UserPreferenceStore(root));
                    var project=KaraokeProject.Create("Alignment",DateTimeOffset.UtcNow) with
                    { Lyrics=new LyricImportService().Parse(string.Join('\n',Enumerable.Repeat("word",50)),".txt",LyricLanguage.English) };
                    Set(window,"_workspace",new WorkspaceSession(root,project));
                    foreach(var method in new[]{"RefreshLyricRows","RefreshVideoRows","RefreshSubtitleStyleFields","RefreshExportPreset"})Call(window,method);
                    await action(window,root,project);completion.TrySetResult();
                }
                catch(Exception e){completion.TrySetException(e);}
                finally
                {
                    if(window is not null){Set(window,"_closingRequested",false);Set(window,"_workspace",null);Set(window,"_allowClose",true);window.Close();}
                    Directory.Delete(root,true);dispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
    static AlignmentOutcome Output(KaraokeProject p,int successful=8)=>new(p.Lyrics.Lines.Select((l,i)=>
        i<successful?new AlignmentLineResult(l.Id,[new(l.Units[0].Id,i*2+1,i*2+2)]):new(l.Id,[],"句内出现零时长、逆序或越界，请手动检查。")).ToArray(),"fixture.log");

    [Fact] public Task ReviewWarningAppearsInRecordingView()=>Sta(async(w,root,p)=>
    {
        await Owned(w,async()=>{var output=Output(p);output=output with {Lines=output.Lines.Select((l,i)=>i==0?l with {Review="AI识别失败，推荐人工审核"}:l).ToArray()};await Apply(w,p,output);});
        Call(w,"ShowRecordingLines",0,0,0);
        Assert.Contains("AI识别失败，推荐人工审核",((System.Windows.Controls.TextBlock)w.FindName("CurrentRecordingLabel")).Text);
        string? destination=Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if(destination is not null)
        {
            w.Width=1440;w.Height=940;w.Show();
            ((System.Windows.Controls.TabControl)w.FindName("EditorTabs")).SelectedItem=w.FindName("RecordingTab");w.UpdateLayout();Set(w,"_browsedRecordingLineId",p.Lyrics.Lines[0].Id);Call(w,"ShowRecordingLines",0,0,0);w.UpdateLayout();
            var content=(System.Windows.FrameworkElement)w.Content;
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(content);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));Directory.CreateDirectory(destination);using var file=File.Create(Path.Combine(destination,"alignment-review.png"));encoder.Save(file);
        }
    });
    [Fact] public Task OwnBusyScopeCommitsSavesAndRetainsNewAssetsWithAccurateSkipCounts()=>Sta(async(w,root,p)=>
    {
        var asset=new MediaAsset(Guid.NewGuid(),"added","media/added.png",MediaKind.Image,ProjectDuration.Zero,"hash");
        await Owned(w,async()=>
        {
            Assert.True((bool)Get(w,"_isBusy")!);
            var denied=Assert.Throws<TargetInvocationException>(()=>Call(w,"CheckAiProject",p.Id));
            Assert.Contains("其他任务",denied.InnerException!.Message);
            var current=(WorkspaceSession)Get(w,"_workspace")!;Set(w,"_workspace",current with {Project=current.Project with {MediaAssets=[asset]}});
            var result=await Apply(w,p,Output(p));
            Assert.Equal(8,result.Applied);Assert.True(result.Saved);Assert.Equal(42,result.Skipped.Length);Assert.Null(result.Error);
            Assert.True((bool)Get(w,"_isBusy")!); // Do not unlock editing to get past the guard.
        });
        var saved=await new JsonProjectRepository().LoadAsync(root);
        Assert.Equal(8,saved.Lyrics.Lines.Count(l=>l.Units.Any(u=>u.IsAutoTimed)));
        Assert.All(saved.Lyrics.Lines.SelectMany(l=>l.Units),u=>Assert.False(u.IsConfirmed));
        Assert.Equal(asset,Assert.Single(saved.MediaAssets));Assert.False((bool)Get(w,"_isBusy")!);
        var undo=(Stack<KaraokeProject>)Get(w,"_undoProjects")!;
        Assert.All(undo.Peek().Lyrics.Lines.SelectMany(l=>l.Units),u=>Assert.False(u.IsAutoTimed));
        Assert.Equal(asset,Assert.Single(undo.Peek().MediaAssets));
    });
    [Theory]
    [InlineData("cancel")]
    [InlineData("closing")]
    [InlineData("switched")]
    [InlineData("edited")]
    public Task ChangedOrCancelledTaskCannotCommit(string reason)=>Sta(async(w,root,p)=>
    {
        await Owned(w,async()=>
        {
            using var cancel=new CancellationTokenSource();
            if(reason=="cancel")cancel.Cancel();
            if(reason=="closing")Set(w,"_closingRequested",true);
            var current=(WorkspaceSession)Get(w,"_workspace")!;
            if(reason=="switched")Set(w,"_workspace",current with {Project=current.Project with {Id=Guid.NewGuid()}});
            if(reason=="edited")Set(w,"_workspace",current with {Project=current.Project with {Title="changed"}});
            if(reason=="cancel")await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Apply(w,p,Output(p),cancel.Token));
            else await Assert.ThrowsAsync<InvalidOperationException>(()=>Apply(w,p,Output(p),cancel.Token));
            Assert.All(((WorkspaceSession)Get(w,"_workspace")!).Project.Lyrics.Lines.SelectMany(l=>l.Units),u=>Assert.False(u.IsAutoTimed));
            Assert.False(File.Exists(Path.Combine(root,"project.kfp")));
            Set(w,"_closingRequested",false);
        });
    });
    [Fact] public Task OtherBusyTaskCannotUseAlignmentCommitPath()=>Sta(async(w,root,p)=>
    {
        Set(w,"_isBusy",true);
        try{await Assert.ThrowsAsync<InvalidOperationException>(()=>Apply(w,p,Output(p)));}
        finally{Set(w,"_isBusy",false);}
    });
    [Fact] public Task AllFailedRowsReturnTheirReasonsWithoutSavingOrChangingLyrics()=>Sta(async(w,root,p)=>
    {
        await Owned(w,async()=>
        {
            var result=await Apply(w,p,Output(p,0));
            Assert.Equal(0,result.Applied);Assert.False(result.Saved);Assert.Equal(50,result.Skipped.Length);Assert.Null(result.Error);
        });
        Assert.Equal(p.Lyrics,((WorkspaceSession)Get(w,"_workspace")!).Project.Lyrics);
        Assert.False(File.Exists(Path.Combine(root,"project.kfp")));
    });
}
