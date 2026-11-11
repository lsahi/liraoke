using KaraokeFilmMaker.Application.Alignment;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;
namespace KaraokeFilmMaker.Application.Tests;
public class AlignmentRecoveryTests
{
    static LyricLine Line()=>new LyricImportService().Parse("one two",".txt",LyricLanguage.English).Lines[0];
    static AlignmentLineResult Result(LyricLine l,double start,double middle,double end)=>new(l.Id,[new(l.Units[0].Id,start,middle-.1),new(l.Units[1].Id,middle,end)]);
    [Fact]public void FillsInternalGapWithoutMovingStartsOrFinalEnd()
    {
        var l=Line();var result=AlignmentEdits.Apply(new(LyricLanguage.English,[l]),new([Result(l,1,2,3)],"log"),10).Document.Lines[0];
        Assert.Equal(2,result.Units[0].Timing.End.TotalSeconds);Assert.Equal(1,result.Units[0].Timing.Start.TotalSeconds);Assert.Equal(3,result.Timing.End.TotalSeconds);
    }
    [Fact]public void IncompleteManualLineCanFillRemainingUnits()
    {
        var l=Line();var manual=l.Units[0] with {Timing=new(ProjectTime.FromSeconds(1),ProjectTime.FromSeconds(2)),IsConfirmed=true,RawInputTime=ProjectTime.FromSeconds(1)};
        l=l with {Units=[manual,l.Units[1]]};
        var outcome=AlignmentEdits.Apply(new(LyricLanguage.English,[l]),new([Result(l,.5,2,3)],"log"),10);
        Assert.Equal(1,outcome.Applied);Assert.Same(manual,outcome.Document.Lines[0].Units[0]);Assert.True(outcome.Document.Lines[0].Units[1].IsAutoTimed);
    }
    [Fact]public void AdjacentOverlapWorksWhileThirdRowIsStillUntimed()
    {
        var a=Line();var b=Line();var c=Line();
        var result=AlignmentEdits.Apply(new(LyricLanguage.English,[a,b,c]),new([Result(a,1,2,4),Result(b,3,4,5)],"log"),10);
        Assert.Equal(2,result.Applied);LyricTimingConflicts.Validate(result.Document);
        Assert.Null(result.Document.Lines[0].ExplicitEndTime);Assert.NotNull(result.Document.Lines[0].AutoAlignmentEnd);Assert.Same(c,result.Document.Lines[2]);
    }
    [Fact]public void TripleOverlapIsNotCommitted()
    {
        var a=Line();var b=Line();var c=Line();
        var result=AlignmentEdits.Apply(new(LyricLanguage.English,[a,b,c]),new([Result(a,1,2,6),Result(b,3,4,7),Result(c,5,6,8)],"log"),10);
        Assert.NotEmpty(result.Skipped);LyricTimingConflicts.Validate(result.Document);
    }
    [Fact]public void ReviewMarkerSurvivesJsonRoundTrip()
    {
        var l=Line();var result=AlignmentEdits.Apply(new(LyricLanguage.English,[l]),new([Result(l,1,2,3) with {Review="AI识别失败，推荐人工审核"}],"log"),10);
        var round=System.Text.Json.JsonSerializer.Deserialize<LyricDocument>(System.Text.Json.JsonSerializer.Serialize(result.Document))!;
        Assert.Equal("AI识别失败，推荐人工审核",round.Lines[0].AlignmentReviewMessage);Assert.Equal(round.Lines[0].Timing.End,round.Lines[0].AutoAlignmentEnd);
    }
}
