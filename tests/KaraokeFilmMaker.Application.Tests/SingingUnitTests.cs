using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Japanese;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class SingingUnitTests
{
    [Theory]
    [InlineData(LyricLanguage.English)]
    [InlineData(LyricLanguage.Japanese)]
    [InlineData(LyricLanguage.Chinese)]
    public void LatinWordsStayTogetherIncludingInMixedLanguageLyrics(LyricLanguage language)
    {
        const string text = "Hello, don't stop! You're my one-and-only. 2026";
        var line = Parse(text, language);
        Assert.Equal(new[] { "Hello", "don't", "stop", "You're", "my", "one-and-only", "2026" }, line.Units.Select(unit => unit.Reading));
        Assert.All(line.Units, unit => Assert.Equal(unit.Reading, string.Concat(unit.DisplayRanges.Select(range => text.Substring(range.Start, range.Length)))));
    }

    [Theory]
    [InlineData("いっしょ", "いっ|しょ")]
    [InlineData("せんじょうがはら", "せん|じょ|う|が|は|ら")]
    [InlineData("ちゃん", "ちゃん")]
    [InlineData("ディスコタイムをスタット", "でぃ|す|こ|た|い|む|を|す|たっ|と")]
    [InlineData("ﾃﾞｨｽｺ", "でぃ|す|こ")]
    [InlineData("スーパー", "すー|ぱー")]
    [InlineData("か、ん", "か|、|ん")]
    public void SingingGroupsFollowUserExamples(string text, string expected)
    {
        Assert.Equal(expected.Split('|'), KanaRomanizer.SplitSingingUnits(text));
    }

    [Fact]
    public async Task WholeExampleMapsKanjiAndOkuriganaAndGroupsAcrossDictionaryBoundaries()
    {
        const string text = "一緒に行きましょうといったら、すぐにディスコタイムをスタット。";
        var source = Parse(text, LyricLanguage.Japanese);
        Assert.NotNull(JapaneseAnnotationService.GetRecordingIssue(new(LyricLanguage.Japanese, [source])));
        var result = await new JapaneseAnnotationService(new ExampleTokens()).AnnotateLineAsync(source);
        Assert.Equal("いっ|しょ|に|い|き|ま|しょ|う|と|いっ|た|ら|す|ぐ|に|でぃ|す|こ|た|い|む|を|す|たっ|と".Split('|'),
            result.Units.Select(unit => unit.Reading));
        Assert.Equal("is", result.Units[0].Romanization);
        Assert.Equal("sho", result.Units[1].Romanization);
        Assert.Equal("it", result.Units[9].Romanization);
        Assert.Equal("di", result.Units[15].Romanization);
        Assert.Equal("tat", result.Units[^2].Romanization);
        Assert.Equal(new TextRange(0, 2), Assert.Single(result.Units[0].DisplayRanges));
        Assert.Equal(new TextRange(0, 2), Assert.Single(result.Units[1].DisplayRanges));
        Assert.Equal("行", Display(result, result.Units[3]));
        Assert.Equal("き", Display(result, result.Units[4]));
        Assert.Equal("ディ", Display(result, result.Units[15]));
        Assert.Null(JapaneseAnnotationService.GetRecordingIssue(new(LyricLanguage.Japanese, [result])));
    }

    [Fact]
    public void CorrectedNameRebuildsUnitsRatherThanOnlyChangingTheAnnotation()
    {
        var source = Parse("戦場ヶ原", LyricLanguage.Japanese);
        var corrected = JapaneseAnnotationService.ApplyManualReading(source, "せんじょうがはら");
        Assert.Equal(new[] { "せん", "じょ", "う", "が", "は", "ら" }, corrected.Units.Select(unit => unit.Reading));
        Assert.Equal(new[] { "sen", "jo", "u", "ga", "ha", "ra" }, corrected.Units.Select(unit => unit.Romanization));
        Assert.All(corrected.Units, unit => Assert.Equal("戦場ヶ原", Display(corrected, unit)));
        Assert.Null(JapaneseAnnotationService.GetRecordingIssue(new(LyricLanguage.Japanese, [corrected])));
    }

    [Fact]
    public void MissingOrStillUnknownJapaneseReadingsCannotBeUsedForRecording()
    {
        var missing = Parse("未知", LyricLanguage.Japanese);
        var unknown = JapaneseAnnotationService.ApplyManualReading(missing, "未知");
        Assert.NotNull(JapaneseAnnotationService.GetRecordingIssue(new(LyricLanguage.Japanese, [unknown])));
        Assert.Null(JapaneseAnnotationService.GetRecordingIssue(new(LyricLanguage.Japanese, [missing with { IsInstrumental = true }])));
        Assert.Null(JapaneseAnnotationService.GetRecordingIssue(new(LyricLanguage.Japanese, [Parse("ちゃんディ", LyricLanguage.Japanese)])));
        Assert.Null(JapaneseAnnotationService.GetRecordingIssue(new(LyricLanguage.Chinese, [missing])));
    }

    private static LyricLine Parse(string text, LyricLanguage language) => new LyricImportService().Parse(text, ".txt", language).Lines[0];
    private static string Display(LyricLine line, LyricUnit unit) => string.Concat(unit.DisplayRanges.Select(range => line.PrimaryText.Substring(range.Start, range.Length)));

    private sealed class ExampleTokens : IJapaneseReadingService
    {
        public Task<IReadOnlyList<JapaneseToken>> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
        {
            List<JapaneseToken> tokens = [];
            int cursor = 0;
            // Deliberately split い and っ across tokens to guard against tokenizer-dependent grouping.
            foreach (var (surface, reading) in new[] { ("一緒", "いっしょ"), ("に", "に"), ("行き", "いき"),
                ("ましょうとい", "ましょうとい"), ("ったら、すぐにディスコタイムをスタット。", "ったら、すぐにディスコタイムをスタット。") })
            {
                tokens.Add(new(surface, reading, KanaRomanizer.Romanize(reading), cursor, surface.Length, true));
                cursor += surface.Length;
            }
            return Task.FromResult<IReadOnlyList<JapaneseToken>>(tokens);
        }
    }
}
