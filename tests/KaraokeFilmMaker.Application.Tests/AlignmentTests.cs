using KaraokeFilmMaker.Application.Alignment;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;
using System.Text.Json;
namespace KaraokeFilmMaker.Application.Tests;
public sealed class AlignmentTests
{
    private static LyricLine Line(string lang="ja",bool manual=false) => new(Guid.NewGuid(),"あい",AnnotationType.None,null,null,new(ProjectTime.Zero,ProjectTime.FromSeconds(1)),
        [new(Guid.NewGuid(),[new(0,1)],"あ",null,new(ProjectTime.Zero,ProjectTime.FromSeconds(.5)),null,manual,lang),new(Guid.NewGuid(),[new(1,1)],"い",null,new(ProjectTime.FromSeconds(.5),ProjectTime.FromSeconds(1)),null,manual,lang)],ReviewState.Unreviewed,ReadingSource.Generated);
    [Theory][InlineData("ja","nextfire")][InlineData("en","qwen")][InlineData("zh","qwen")]
    public void RoutesByPreparedUnits(string lang,string plugin) => Assert.Equal(plugin,AlignmentPlugins.Preferred(new(LyricLanguage.Auto,[Line(lang)])));
    [Fact]public void AppliesCorrectedUnitsWithoutChangingReadingIdentityOrMarkingManual()
    {
        var l=Line();var doc=new LyricDocument(LyricLanguage.Japanese,[l]);
        var result=AlignmentEdits.Apply(doc,new([new(l.Id,[new(l.Units[0].Id,2,2.5),new(l.Units[1].Id,2.5,3)])],"log"),10);
        Assert.Equal(1,result.Applied);Assert.Equal(l.Units.Select(u=>u.Id),result.Document.Lines[0].Units.Select(u=>u.Id));
        Assert.Equal(l.ReadingSource,result.Document.Lines[0].ReadingSource);Assert.Equal(l.PrimaryText,result.Document.Lines[0].PrimaryText);
        Assert.All(result.Document.Lines[0].Units,u=>{Assert.False(u.IsConfirmed);Assert.Null(u.RawInputTime);});
        Assert.Equal(2,result.Document.Lines[0].Timing.Start.TotalSeconds);Assert.Equal(0,doc.Lines[0].Timing.Start.TotalSeconds);
    }
    [Fact]public void ProtectsManualTimes()
    {
        var l=Line(manual:true);var result=AlignmentEdits.Apply(new(LyricLanguage.Japanese,[l]),new([new(l.Id,[new(l.Units[0].Id,2,3),new(l.Units[1].Id,3,4)])],"log"),10);
        Assert.Equal(0,result.Applied);Assert.Same(l,result.Document.Lines[0]);
    }
    [Theory][InlineData(2,2)][InlineData(3,2)][InlineData(2,11)][InlineData(double.NaN,3)]
    public void RejectsInvalidPointsWithoutPartialLineEdits(double start,double end)
    {
        var l=Line();var result=AlignmentEdits.Apply(new(LyricLanguage.Japanese,[l]),new([new(l.Id,[new(l.Units[0].Id,start,end),new(l.Units[1].Id,3,4)])],"log"),10);
        Assert.Equal(0,result.Applied);Assert.Same(l,result.Document.Lines[0]);Assert.Single(result.Skipped);
    }
    [Fact]public void PartialRerunCannotOverwriteOrOverlapPreviousAutomaticRows()
    {
        var first=Line(); var second=Line();
        second=second with { Units=second.Units.Select(u=>u with {IsAutoTimed=true}).ToArray() };
        var result=AlignmentEdits.Apply(new(LyricLanguage.Japanese,[first,second]),
            new([new(first.Id,[new(first.Units[0].Id,0,.6),new(first.Units[1].Id,.6,1.2)]),new(second.Id,[],"model failed")],"log"),10);
        Assert.Equal(0,result.Applied);Assert.Same(first,result.Document.Lines[0]);Assert.Same(second,result.Document.Lines[1]);
    }
    [Fact]public async Task InstallerToolRejectsUnknownPluginsAndArbitraryPaths()
    {
        bool called=false;var bridge=new AiToolBridge((_,_,_)=>{called=true;return Task.FromResult<object>(new{});});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>bridge.ExecuteAsync("install_alignment_plugin",JsonDocument.Parse("{\"pluginId\":\"download-anything\",\"device\":\"cpu\"}").RootElement,default));
        Assert.False(called);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>bridge.ExecuteAsync("install_alignment_plugin",JsonDocument.Parse("{\"pluginId\":\"qwen\",\"device\":\"cpu\",\"path\":\"x\"}").RootElement,default));
    }
}
