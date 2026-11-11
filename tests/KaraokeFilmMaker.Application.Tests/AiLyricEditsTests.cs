using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
namespace KaraokeFilmMaker.Application.Tests;
public class AiLyricEditsTests
{
    private readonly LanguageAnalysisService analysis=new(LanguageRegistry.CreateBuiltIns(new Reader()));
    private sealed class Reader:IJapaneseReadingService
    {public Task<IReadOnlyList<JapaneseToken>> AnalyzeAsync(string text,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<JapaneseToken>>([new(text,text,"",0,text.Length,false)]);}
    [Theory][InlineData("replace")][InlineData("insert_before")][InlineData("insert_after")][InlineData("delete")][InlineData("move")]
    public async Task EditsOnlyTargetAndKeepsOtherLine(string action)
    {
        var d=new LyricImportService().Parse("hello\nworld",".txt",LyricLanguage.English);var target=d.Lines[0];
        var result=await AiLyricEdits.PrepareAsync(d,analysis,action,target.Id,"hello","changed",null,0,0,2,default);
        Assert.Contains(result.Lines,l=>ReferenceEquals(l,d.Lines[1]));Assert.Equal("hello",target.PrimaryText);
        Assert.Equal(action=="delete"?1:action.StartsWith("insert")?3:2,result.Lines.Count);
    }
    [Fact]public async Task StaleTextRejected()
    {
        var d=new LyricImportService().Parse("hello",".txt",LyricLanguage.English);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>AiLyricEdits.PrepareAsync(d,analysis,"delete",d.Lines[0].Id,"wrong",null,null,0,0,0,default));
    }
    [Fact]public async Task ReadingChangesOnlySelectedOccurrence()
    {
        var d=await analysis.AnalyzeDocumentAsync(new LyricImportService().Parse("あい\nあい",".txt",LyricLanguage.Japanese));
        var result=await AiLyricEdits.PrepareAsync(d,analysis,"reading",d.Lines[0].Id,"あい",null,"う",0,1,0,default);
        Assert.Same(d.Lines[1],result.Lines[1]);Assert.Contains(result.Lines[0].Units,u=>u.Reading=="う");
    }
}
