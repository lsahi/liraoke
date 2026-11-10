using System.Text.Json;
using KaraokeFilmMaker.Application.Ai;
namespace KaraokeFilmMaker.Application.Tests;
public sealed class AiToolBridgeTests
{
    [Theory]
    [InlineData("shell","{}")]
    [InlineData("update_subtitle_style","{\"patch\":{\"safeAreaRatio\":0.9}}")]
    [InlineData("update_song_title","{\"patch\":{\"fontSize\":null}}")]
    [InlineData("get_lyrics","{\"count\":51}")]
    [InlineData("set_visualization","{\"mode\":\"Fireworks\"}")]
    public async Task RejectsArgumentsBeforeDispatch(string name,string json)
    {
        var bridge=new AiToolBridge((_,_,_)=>throw new Exception("must not execute"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>bridge.ExecuteAsync(name,JsonDocument.Parse(json).RootElement,default));
    }
    [Fact] public async Task DispatchesAllowedPatch()
    {
        var bridge=new AiToolBridge((name,args,_)=>Task.FromResult<object>(args.GetProperty("patch").GetProperty("primaryFontSize").GetDouble()));
        Assert.Equal(80d,await bridge.ExecuteAsync("update_subtitle_style",JsonDocument.Parse("{\"patch\":{\"primaryFontSize\":80}}").RootElement,default));
    }
}
