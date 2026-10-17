using KaraokeFilmMaker.Application.Japanese;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class LyricUnitEditorTests
{
    [Fact]
    public void SharedKanjiCorrectionPreservesAllIdsAndTimingsAndShiftsFollowingText()
    {
        var source = new LyricImportService().Parse("[00:01]一緒に", ".lrc", LyricLanguage.Japanese).Lines[0];
        var line = JapaneseAnnotationService.ApplyManualReading(source, "いっしょに");
        var units = line.Units.Select(unit => unit with { RawInputTime = unit.Timing.Start, IsConfirmed = true }).ToArray();
        line = line with { Units = units };
        var result = LyricUnitEditor.Edit(line, units[1].Id, "いっしょ", "しゃ", LyricLanguage.Japanese);
        Assert.Equal("いっしょに", result.PrimaryText);
        Assert.Equal("いっしゃに", result.ReadingText);
        Assert.Equal(new TextRange(0, 4), result.Units[0].DisplayRanges.Single());
        Assert.Equal(new TextRange(0, 4), result.Units[1].DisplayRanges.Single());
        Assert.Equal(new TextRange(4, 1), result.Units[2].DisplayRanges.Single());
        Assert.Equal(units.Select(unit => (unit.Id, unit.Timing, unit.RawInputTime, unit.IsConfirmed)),
            result.Units.Select(unit => (unit.Id, unit.Timing, unit.RawInputTime, unit.IsConfirmed)));
        Assert.Equal("sha", result.Units[1].Romanization);
        Assert.Null(JapaneseAnnotationService.GetRecordingIssue(new(LyricLanguage.Japanese, [result])));
    }

    [Theory]
    [InlineData("一緒", "issho")]
    [InlineData("一緒", "いっしょ")]
    [InlineData("", "いっ")]
    [InlineData("一\n緒", "いっ")]
    public void InvalidQuickCorrectionsDoNotSilentlyRegroupOrDropTimings(string text, string reading)
    {
        var line = JapaneseAnnotationService.ApplyManualReading(
            new LyricImportService().Parse("一緒", ".txt", LyricLanguage.Japanese).Lines[0], "いっしょ");
        Assert.Throws<InvalidOperationException>(() => LyricUnitEditor.Edit(line, line.Units[0].Id, text, reading, LyricLanguage.Japanese));
    }

    [Theory]
    [InlineData(LyricLanguage.English, "Hello world", "Hi", "Hi world", 3)]
    [InlineData(LyricLanguage.Chinese, "春风", "清", "清风", 1)]
    public void OriginalCorrectionFollowsTextForChineseAndEnglish(LyricLanguage language, string original, string replacement, string expected, int nextOffset)
    {
        var line = new LyricImportService().Parse(original, ".txt", language).Lines[0];
        var result = LyricUnitEditor.Edit(line, line.Units[0].Id, replacement, line.Units[0].Reading, language);
        Assert.Equal(expected, result.PrimaryText); Assert.Equal(replacement, result.Units[0].Reading);
        Assert.Equal(nextOffset, result.Units[1].DisplayRanges[0].Start);
        Assert.Equal(line.Units.Select(unit => unit.Id), result.Units.Select(unit => unit.Id));
    }
}
