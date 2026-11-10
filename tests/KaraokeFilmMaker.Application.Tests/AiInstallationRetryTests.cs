using System.Text.Json;
using KaraokeFilmMaker.Application.Ai;

namespace KaraokeFilmMaker.Application.Tests;
public sealed class AiInstallationRetryTests
{
    static JsonElement Args(string id,string device)=>JsonSerializer.SerializeToElement(new {pluginId=id,device});
    [Theory]
    [InlineData("failed")]
    [InlineData("declined")]
    [InlineData("completed")]
    [InlineData("exception")]
    public async Task SamePluginCannotRequestASecondApprovalEvenWhenDeviceChanges(string firstOutcome)
    {
        int calls=0;
        var bridge=new AiToolBridge((_,_,_)=>
        {
            calls++;
            if(firstOutcome=="exception")throw new IOException("download failed");
            return Task.FromResult<object>(new {installed=firstOutcome=="completed",approved=firstOutcome!="declined"});
        });
        if(firstOutcome=="exception")await Assert.ThrowsAsync<IOException>(()=>bridge.ExecuteAsync("install_alignment_plugin",Args("qwen","gpu"),default));
        else await bridge.ExecuteAsync("install_alignment_plugin",Args("qwen","gpu"),default);
        foreach(var device in new[]{"cpu","gpu"})
        {
            var result=JsonSerializer.SerializeToElement(await bridge.ExecuteAsync("install_alignment_plugin",Args("qwen",device),default));
            Assert.True(result.GetProperty("blocked").GetBoolean());Assert.True(result.GetProperty("requiresUserReply").GetBoolean());
        }
        Assert.Equal(1,calls);
    }
    [Fact]public async Task OtherPluginAndNewUserTurnRemainAvailable()
    {
        int calls=0;Task<object> Execute(string name,JsonElement args,CancellationToken token){calls++;return Task.FromResult<object>(new {installed=false});}
        var first=new AiToolBridge(Execute);
        await first.ExecuteAsync("install_alignment_plugin",Args("qwen","gpu"),default);
        await first.ExecuteAsync("install_alignment_plugin",Args("nextfire","gpu"),default);
        await new AiToolBridge(Execute).ExecuteAsync("install_alignment_plugin",Args("qwen","gpu"),default);
        Assert.Equal(3,calls);
    }
    [Fact]public async Task ConcurrentCallsCannotCreateDuplicateApprovalRequests()
    {
        int calls=0;var completion=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge=new AiToolBridge((_,_,_)=>{Interlocked.Increment(ref calls);return completion.Task;});
        var requests=Enumerable.Range(0,12).Select(i=>Task.Run(()=>bridge.ExecuteAsync("install_alignment_plugin",Args("qwen",i%2==0?"gpu":"cpu"),default))).ToArray();
        completion.SetResult(new {installed=false});await Task.WhenAll(requests);Assert.Equal(1,calls);
    }
}
