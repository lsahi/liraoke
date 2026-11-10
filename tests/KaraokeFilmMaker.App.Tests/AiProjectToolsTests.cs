using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Infrastructure.Persistence;
namespace KaraokeFilmMaker.App.Tests;
public sealed class AiProjectToolsTests
{
    [Fact] public Task StyleEditsAreValidatedUndoableAndProjectBound()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{
            string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));MainWindow? window=null;
            try{
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                window=new MainWindow(new UserPreferenceStore(root));var project=KaraokeProject.Create("Test",DateTimeOffset.UtcNow);
                var field=typeof(MainWindow).GetField("_workspace",BindingFlags.NonPublic|BindingFlags.Instance)!;
                field.SetValue(window,new WorkspaceSession(root,project));
                object Call(string name,string json,Guid? id=null)=>((Task<object>)typeof(MainWindow).GetMethod("ExecuteAiToolCoreAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,[id??project.Id,name,JsonDocument.Parse(json).RootElement,CancellationToken.None])!).GetAwaiter().GetResult();
                Call("update_subtitle_style","{\"patch\":{\"primaryFontSize\":80}}");
                var current=(WorkspaceSession)field.GetValue(window)!;Assert.Equal(80,current.Project.SubtitleStyle.PrimaryFontSize);
                Assert.Equal(project.Lyrics,current.Project.Lyrics);
                var undo=(Stack<KaraokeProject>)typeof(MainWindow).GetField("_undoProjects",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
                Assert.Single(undo);Assert.Equal(project,undo.Peek());
                Assert.Throws<InvalidOperationException>(()=>Call("update_subtitle_style","{\"patch\":{\"primaryFontSize\":-10}}"));
                Assert.Equal(current,field.GetValue(window));
                Assert.Throws<InvalidOperationException>(()=>Call("get_project_context","{}",Guid.NewGuid()));
                string context=JsonSerializer.Serialize(Call("get_project_context","{}"));Assert.DoesNotContain(root,context);
                done.SetResult();
            }catch(Exception e){done.SetException(e);}
            finally{if(window is not null){typeof(MainWindow).GetField("_workspace",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,null);window.Close();}if(Directory.Exists(root))Directory.Delete(root,true);}
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
