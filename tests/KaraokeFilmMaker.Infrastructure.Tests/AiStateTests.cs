using System.Text;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Infrastructure.Ai;
namespace KaraokeFilmMaker.Infrastructure.Tests;
public sealed class AiStateTests
{
    [Fact] public void InterruptedConversationAndUserMemorySurviveReopenWithoutLeakingPlaintext()
    {
        string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            var store=new AiStateStore(root);
            store.SaveSession("provider-a",[new("user","请安装日语模型"),new("context","安装已批准，结果未知")],true);
            var memory=store.Remember("优先使用日语歌曲和 GPU");
            var reopened=new AiStateStore(root);Assert.True(reopened.LoadSession("provider-a").InFlight);Assert.Equal(2,reopened.LoadSession("provider-a").Messages.Count);
            Assert.Empty(reopened.LoadSession("provider-b").Messages);Assert.Equal(memory,Assert.Single(reopened.Memories()));
            Assert.All(Directory.GetFiles(root),file=>Assert.DoesNotContain("日语",Encoding.UTF8.GetString(File.ReadAllBytes(file))));
            reopened.ArchiveSession("provider-a");Assert.Empty(reopened.LoadSession("provider-a").Messages);Assert.Single(reopened.Memories());
            reopened.Forget(memory.Id);Assert.Empty(new AiStateStore(root).Memories());
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact] public void ContextBudgetKeepsLatestToolOutcomeAndDoesNotMutateArchive()
    {
        var history=Enumerable.Range(0,200).Select(i=>new AiChatMessage("user",$"request-{i}:"+new string('a',3000))).ToList();
        history.Add(new("context","Qwen installed successfully"));
        var context=AiContextWindow.Select(history,10000);
        Assert.Contains(context,m=>m.Text=="Qwen installed successfully");Assert.True(context.Sum(m=>m.Text.Length)<15000);Assert.Equal(201,history.Count);
        Assert.Contains(context,m=>m.Text.Contains("早期用户请求"));
    }
}
