using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;
public sealed class ReadingCorrectionTests
{
    private static LyricLine Line(string text) => new LyricImportService().Parse(text, ".txt", LyricLanguage.Japanese).Lines[0];
    private static LanguageAnalysisService Service() => new(LanguageRegistry.CreateBuiltIns(new Reader()));
    [Fact] public async Task SourceRubyAndCrossTokenSingingGroupsStayIndependent()
    {
        var line = Line("走って");
        var proposal = new ReadingProposal(line.Id, line.PrimaryText, line.PrimaryText,
            [new("走", "はし"), new("って", "って")]);
        var result = await ReadingCorrection.ApplyAsync(line, proposal, Service());
        Assert.Equal(new[] { "は", "しっ", "て" }, result.Units.Select(u => u.Reading));
        Assert.Contains(result.Presentation!.SubtitleRuns, r => r.Range == new TextRange(0, 1) && r.Annotation == "はし");
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Japanese, [result])));
        Assert.Empty(result.AiReadingReview!.Issues);
        Assert.Equal("applied", result.AiReadingReview.Status);
        Assert.All(result.Units, u => Assert.False(u.IsConfirmed));
    }
    [Fact] public async Task NearSoundSuggestionDoesNotRequireLiteralReadingEquality()
    {
        var line = Line("墨");
        var result = await ReadingCorrection.ApplyAsync(line, new(line.Id, line.PrimaryText, "墨",
            [new("墨", "すみ", Uncertain: false, Evidence: "つみ")]), Service(), [new(0, 10, "ゆきはつみにとけて")]);
        Assert.Equal("すみ", result.ReadingText);
        Assert.Empty(result.AiReadingReview!.Issues);
        Assert.NotEqual(ReadingSource.Confirmed, result.ReadingSource);
        Assert.Equal(ReviewState.Unreviewed, result.ReviewState);
    }
    [Fact] public async Task AtypicalReadingCanBeAppliedButTextModelConfidenceIsNotProof()
    {
        var line = Line("星空");
        var result = await ReadingCorrection.ApplyAsync(line, new(line.Id, line.PrimaryText, "星空",
            [new("星空", "なさ", Uncertain: false, Evidence: "なさ")]), Service(), [new(0, 10, "星空")]);
        Assert.Equal("なさ", result.ReadingText);
        Assert.Empty(result.AiReadingReview!.Issues);
        Assert.Equal("applied", result.AiReadingReview.Status);
        Assert.NotEqual(ReadingSource.Confirmed, result.ReadingSource);
    }
    [Fact] public async Task ManualAndTimedLinesRemainIntactWithVisibleSuggestion()
    {
        var line = Line("星空") with { ReadingSource = ReadingSource.Manual };
        var result = await ReadingCorrection.ApplyAsync(line, new(line.Id, "星空", "星空", [new("星空", "なさ")]), Service());
        Assert.Same(line.Units, result.Units); Assert.Equal(line.Timing, result.Timing);
        Assert.Equal("protected", result.AiReadingReview!.Status);
        Assert.Equal("なさ", Assert.Single(result.AiReadingReview.Suggestions).Reading);
        var timed = line with { ReadingSource = null, Units = line.Units.Select(u => u with { IsConfirmed = true }).ToArray() };
        Assert.True(ReadingCorrection.IsProtected(timed));
    }
    [Fact] public async Task MixedEnglishWordsAndChineseKeepTheirOwnUnits()
    {
        var line = Line("君 love you 我们");
        var result = await ReadingCorrection.ApplyAsync(line, new(line.Id, line.PrimaryText, line.PrimaryText,
            [new("君", "きみ"), new(" love you ", "love you", "en"), new("我们", "我们", "zh")]), Service());
        Assert.Equal(new[] { "love", "you" }, result.Units.Where(u => u.LanguageId == "en").Select(u => u.Reading));
        Assert.Equal(2, result.Units.Count(u => u.LanguageId == "zh"));
    }
    [Fact] public void InvalidOrStaleMappingsAreRejected()
    {
        var line = Line("星空");
        Assert.Throws<ReadingMappingException>(() => ReadingCorrection.Validate(new(line.Id, "星", "星空", [new("星空", "なさ")]), line));
        Assert.Throws<ReadingMappingException>(() => ReadingCorrection.Validate(new(line.Id, "星空", "星空", [new("星", "なさ")]), line));
        Assert.Throws<ReadingMappingException>(() => ReadingCorrection.Validate(new(line.Id, "星空", "星空", [new("星空", "nasa")]), line));
    }
    [Fact] public async Task ParentheticalReadingCanBeRemovedWithoutAddingTiming()
    {
        var line = Line("星空（なさ）");
        var result = await ReadingCorrection.ApplyAsync(line, new(line.Id, line.PrimaryText, "星空", [new("星空", "なさ")]), Service());
        Assert.Equal("星空", result.PrimaryText); Assert.Equal(line.PrimaryText, result.AiReadingReview!.OriginalText);
        Assert.Null(result.ExplicitEndTime); Assert.All(result.Units, u => Assert.Null(u.RawInputTime));
    }
    [Theory] [InlineData("おぼろげ", "なまま幸せそうに", "なまましあわせそうに")]
    [InlineData("みなも", "は景色を変えた", "はけしきをかえた")]
    public async Task SungParenthesesKeepWordsButNotBracketSounds(string inside, string tail, string reading)
    {
        var line = Line("(" + inside + ")" + tail);
        var result = await ReadingCorrection.ApplyAsync(line, new(line.Id, line.PrimaryText, line.PrimaryText,
            [new("(", ""), new(inside, inside), new(")", ""), new(tail, reading)]), Service());
        Assert.Equal(line.PrimaryText, result.PrimaryText);
        Assert.Equal(inside + reading, string.Concat(result.Units.Select(u => u.Reading)));
        Assert.DoesNotContain(result.Units, u => u.Reading!.Contains('(') || u.Reading.Contains(')'));
    }
    [Fact] public async Task AnnotationParenthesesDoNotDuplicateSungReading()
    {
        var line = Line("風が吹き灯火(ともしび)が揺れた");
        var result = await ReadingCorrection.ApplyAsync(line, new(line.Id, line.PrimaryText, line.PrimaryText,
            [new("風が吹き", "かぜがふき"), new("灯火", "ともしび"), new("(ともしび)", "", NotSung: true), new("が揺れた", "がゆれた")]), Service());
        Assert.Equal("風が吹き灯火が揺れた", result.PrimaryText);
        Assert.DoesNotContain(result.Presentation!.SubtitleRuns, x => result.PrimaryText.Substring(x.Range.Start,x.Range.Length).Contains('('));
        Assert.Equal("かぜがふきともしびがゆれた", string.Concat(result.Units.Select(u => u.Reading)));
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Japanese, [result])));
    }
    [Fact] public void CannotSilenceOrdinaryWordsAsAnnotations()
    {
        var line = Line("灯火");
        Assert.Throws<ReadingMappingException>(() => ReadingCorrection.Validate(new(line.Id,line.PrimaryText,line.PrimaryText,
            [new("灯火", "", NotSung: true)]),line));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SplitOrCombinedAnnotationUsesAtejiReadingAndRemovesAllBracketText(bool combined)
    {
        var line = Line("消えてゆく夢幻（ゆめまぼろし）");
        ReadingSegment[] mapping = combined
            ? [new("消えてゆく", "きえてゆく"), new("夢幻（ゆめまぼろし）", "むげん", NotSung:true)]
            : [new("消えてゆく", "きえてゆく"), new("夢幻", "むげん"), new("（", ""), new("ゆめまぼろし", "ゆめまぼろし", NotSung:true), new("）", "")];
        var result = await ReadingCorrection.ApplyAsync(line, new(line.Id,line.PrimaryText,line.PrimaryText,mapping),Service());
        Assert.Equal("消えてゆく夢幻",result.PrimaryText);
        Assert.Equal("きえてゆくゆめまぼろし",string.Concat(result.Units.Select(u=>u.Reading)));
        Assert.Equal(line.PrimaryText,result.AiReadingReview!.OriginalText);
        Assert.Contains(result.Presentation!.SubtitleRuns,x=>x.Annotation == "ゆめまぼろし");
        Assert.All(result.Units,u=>Assert.All(u.DisplayRanges,r=>Assert.True(r.EndExclusive<=result.PrimaryText.Length)));
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Japanese,[result])));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SungRepeatIsNotRemovedJustBecauseItMatchesPreviousReading(bool punctuationNotSung)
    {
        var line=Line("君(きみ)");
        var result=await ReadingCorrection.ApplyAsync(line,new(line.Id,line.PrimaryText,line.PrimaryText,
            [new("君","きみ"),new("(","",NotSung:punctuationNotSung),new("きみ","きみ"),new(")","",NotSung:punctuationNotSung)]),Service());
        Assert.Equal(line.PrimaryText,result.PrimaryText);
        Assert.Equal("きみきみ",string.Concat(result.Units.Select(u=>u.Reading)));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task RepeatedCleanLyricCorroboratesSplitKanjiAnnotation(bool hasCleanCounterpart)
    {
        var line=Line("夢は夢で夢は逆夢(さかゆめ)");
        var proposal=new ReadingProposal(line.Id,line.PrimaryText,line.PrimaryText,
            [new("夢","ゆめ"),new("は","は"),new("夢","ゆめ"),new("で","で"),new("夢","ゆめ"),new("は","は"),
             new("逆","さか"),new("夢","ゆめ"),new("(",""),new("さかゆめ","さかゆめ"),new(")","")]);
        proposal=ReadingCorrection.NormalizeAnnotations(proposal,hasCleanCounterpart ? ["夢は夢で夢は逆夢"] : [line.PrimaryText]);
        var result=await ReadingCorrection.ApplyAsync(line,proposal,Service());
        Assert.Equal(hasCleanCounterpart ? "夢は夢で夢は逆夢" : line.PrimaryText,result.PrimaryText);
        Assert.Equal("ゆめはゆめでゆめはさかゆめ"+(hasCleanCounterpart ? "" : "さかゆめ"),string.Concat(result.Units.Select(u=>u.Reading)));
        Assert.Equal("applied",result.AiReadingReview!.Status);
    }
    private sealed class Reader : IJapaneseReadingService
    {
        public Task<IReadOnlyList<JapaneseToken>> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<JapaneseToken>>([new(text, text, "", 0, text.Length, false)]);
    }
}
