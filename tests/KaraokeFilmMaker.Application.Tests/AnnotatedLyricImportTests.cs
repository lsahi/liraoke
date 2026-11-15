using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class AnnotatedLyricImportTests
{
    private readonly LyricImportService _import = new();

    [Theory]
    [InlineData(".txt")]
    [InlineData(".lrc")]
    public async Task UntimedAnnotatedLyricsPreserveAtejiWithoutDictionary(string extension)
    {
        var reader = new Reader();
        var service = new LanguageAnalysisService(LanguageRegistry.CreateBuiltIns(reader));
        var parsed = _import.Parse("\uFEFF[tool:utaten-ruby]\n[ti:Example]\n[ar:Author]\n{夢幻||ユメマボロシ}\n{灯火||ともしび}", extension, LyricLanguage.Auto);
        Assert.Equal(["夢幻", "灯火"], parsed.Lines.Select(line => line.PrimaryText));
        Assert.Equal("ゆめまぼろし", string.Concat(parsed.Lines[0].Units.Select(unit => unit.Reading)));
        var result = await service.AnalyzeDocumentAsync(parsed);
        Assert.Empty(reader.Requests);
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(result));
        Assert.Equal("ゆめまぼろし", result.Lines[0].ReadingText);
        Assert.Equal("ゆめまぼろし", Assert.Single(result.Lines[0].Presentation!.SubtitleRuns).Annotation);
        Assert.All(result.Lines[0].Units, unit => Assert.Equal(new TextRange(0, 2), Assert.Single(unit.DisplayRanges)));
        Assert.All(result.Lines.SelectMany(line => line.Units), unit =>
        { Assert.False(unit.IsConfirmed); Assert.Null(unit.RawInputTime); Assert.Equal(0, unit.Timing.Duration.Microseconds); });
        Assert.Same(result.Lines[0], (await service.AnalyzeDocumentAsync(result)).Lines[0]);
        var repeated = await service.AnalyzeDocumentAsync(result, force: true);
        Assert.Equal("ゆめまぼろし", repeated.Lines[0].ReadingText);
        Assert.Empty(reader.Requests);
    }

    [Fact]
    public async Task OnlyUnannotatedJapaneseUsesDictionaryAndEnglishKeepsWordUnits()
    {
        var reader = new Reader();
        var service = new LanguageAnalysisService(LanguageRegistry.CreateBuiltIns(reader));
        var result = await service.AnalyzeDocumentAsync(_import.Parse("{灯火||ともしび} 世界 hello world", ".txt", LyricLanguage.Japanese));
        var line = Assert.Single(result.Lines);
        Assert.DoesNotContain(reader.Requests, text => text.Contains("灯火"));
        Assert.Contains(reader.Requests, text => text.Contains("世界"));
        Assert.Equal("ともしび", string.Concat(line.Units.Where(unit => unit.DisplayRanges.Any(r => r.Start == 0)).Select(u => u.Reading)));
        Assert.Equal(["hello", "world"], line.Units.Where(u => u.LanguageId == LanguageIds.English).Select(u => u.Reading));
        Assert.Equal(line.PrimaryText, line.Presentation!.SourceText);
        Assert.All(line.Units.SelectMany(u => u.DisplayRanges), r => Assert.InRange(r.EndExclusive, 1, line.PrimaryText.Length));
    }

    [Fact]
    public async Task ExplicitCharacterGroupsAndEmptyContinuationKeepTheirRanges()
    {
        var service = new LanguageAnalysisService(LanguageRegistry.CreateBuiltIns(new Reader()));
        var result = await service.AnalyzeDocumentAsync(_import.Parse("{物語||もの,がたり}{今日||きょう,}", ".txt", LyricLanguage.Japanese));
        var line = result.Lines[0];
        Assert.Equal("物語今日", line.PrimaryText);
        Assert.Equal([new TextRange(0, 1), new(1, 1), new(2, 2)], line.LanguageSpans!.Select(s => s.Range));
        Assert.Equal("ものがたりきょう", line.ReadingText);
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(result));
    }

    [Theory]
    [InlineData("{灯火||}")]
    [InlineData("{灯火||ともしび")]
    [InlineData("{||かな}")]
    [InlineData("{夢幻||夢幻}")]
    [InlineData("{物語||もの,が,たり}")]
    [InlineData("{今日||,きょう}")]
    [InlineData("{灯火||と|もしび}")]
    [InlineData("{{灯火||ともしび}")]
    public void InvalidAnnotationsFailBeforeReplacingLyrics(string text)
        => Assert.Throws<FormatException>(() => _import.Parse(text, ".txt", LyricLanguage.Japanese));

    [Fact]
    public void OrdinaryBracesAndParenthesesRemainLiteral()
    {
        const string text = "灯火（ともしび） (hello) {chorus}";
        var line = _import.Parse(text, ".txt", LyricLanguage.Japanese).Lines[0];
        Assert.Equal(text, line.PrimaryText);
        Assert.Null(line.LanguageSpans);
    }

    [Fact]
    public void SupplementaryCharactersUseUtf16RangesAndDoNotShiftFollowingText()
    {
        var parsed = AnnotatedLyricText.Parse("🎵{𠮷野||よしの} {灯火||ともしび}");
        Assert.Equal("🎵𠮷野 灯火", parsed.Text);
        Assert.Equal([new TextRange(2, 3), new(6, 2)], parsed.Readings.Select(r => r.Range));
    }

    [Fact]
    public void LrcTimesAndRepeatedLinesSurviveMarkupRemoval()
    {
        var lines = _import.Parse("[ti:Song]\n[00:01.25][00:06.00]{灯火||ともしび}\n[00:03.50]{夢幻||ゆめまぼろし}", ".lrc", LyricLanguage.Japanese).Lines;
        Assert.Equal(["灯火", "夢幻", "灯火"], lines.Select(l => l.PrimaryText));
        Assert.Equal([1_250_000L, 3_500_000L, 6_000_000L], lines.Select(l => l.Timing.Start.Microseconds));
        Assert.Equal(3_500_000, lines[0].Timing.End.Microseconds);
        Assert.NotEqual(lines[0].Id, lines[2].Id);
    }

    [Fact]
    public void MixedTimedAndUntimedAnnotatedLrcIsNotSilentlyTruncated()
        => Assert.Throws<FormatException>(() => _import.Parse("[00:01.00]{灯火||ともしび}\n{夢幻||ゆめまぼろし}", ".lrc", LyricLanguage.Japanese));

    [Fact]
    public void AnnotatedSrtPreservesTimesAndMalformedInputIdentifiesSentence()
    {
        var line = _import.Parse("1\n00:00:02,100 --> 00:00:04,250\n{灯火||ともしび}\n", ".srt", LyricLanguage.Japanese).Lines[0];
        Assert.Equal("灯火", line.PrimaryText);
        Assert.Equal("ともしび", string.Concat(line.Units.Select(u => u.Reading)));
        Assert.Equal(2_100_000, line.Timing.Start.Microseconds);
        Assert.Equal(4_250_000, line.Timing.End.Microseconds);
        Assert.Contains("第 2 句", Assert.Throws<FormatException>(() =>
            _import.Parse("hello\n{灯火||}", ".txt", LyricLanguage.Auto)).Message);
    }

    [Fact]
    public async Task AiImportUsesTheSameOfflineMapping()
    {
        var reader = new Reader();
        var result = await AiLyricEdits.PrepareAsync(new(LyricLanguage.Auto, []),
            new LanguageAnalysisService(LanguageRegistry.CreateBuiltIns(reader)), "txt", null, null,
            "{灯火||ともしび}", null, 0, 0, 0, default);
        Assert.Equal("ともしび", result.Lines[0].ReadingText);
        Assert.Empty(reader.Requests);
    }

    private sealed class Reader : IJapaneseReadingService
    {
        public List<string> Requests { get; } = [];
        public Task<IReadOnlyList<JapaneseToken>> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
        {
            Requests.Add(text);
            string reading = text.Replace("世界", "せかい", StringComparison.Ordinal);
            return Task.FromResult<IReadOnlyList<JapaneseToken>>([new(text, reading, "", 0, text.Length, false)]);
        }
    }
}
