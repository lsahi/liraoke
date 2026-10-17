using KaraokeFilmMaker.Application.Japanese;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class JapaneseRubyTests
{
    [Theory]
    [InlineData("走っ", "はしっ", "走", "はし")]
    [InlineData("待っ", "まっ", "待", "ま")]
    [InlineData("進む", "すすむ", "進", "すす")]
    [InlineData("行き", "いき", "行", "い")]
    [InlineData("本ん", "ほんん", "本", "ほん")]
    [InlineData("走ッ", "はしっ", "走", "はし")]
    public void SubtitleRubyExcludesWrittenKanaEvenWhenTimingUnitsCrossTheBoundary(string text, string reading, string kanji, string ruby)
    {
        var source = new LyricImportService().Parse(text, ".txt", LyricLanguage.Japanese).Lines[0];
        // Reproduce old saved groups, including shared ranges crossing a written-kana boundary.
        var line = JapaneseAnnotationService.ApplyManualReading(source, reading);
        line = line with { Units = line.Units.Select(unit => unit with
        { DisplayRanges = [new TextRange(0, text.Length)], IsConfirmed = true, RawInputTime = unit.Timing.Start }).ToArray() };
        string before = System.Text.Json.JsonSerializer.Serialize(line);
        var display = JapaneseRubyBuilder.Build(line, false);
        Assert.Equal(text, string.Concat(display.Select(group => group.Text)));
        var annotated = Assert.Single(display, group => group.Annotation.Length > 0);
        Assert.Equal(kanji, annotated.Text); Assert.Equal(ruby, annotated.Annotation);
        Assert.Equal(new TextRange(0, kanji.Length), annotated.Range);
        Assert.Single(JapaneseRubyBuilder.BuildTimingGroups(line, false));
        Assert.Equal(reading, JapaneseRubyBuilder.BuildTimingGroups(line, false)[0].Annotation);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(line));
        Assert.Equal(new TextRange(0, text.Length), LyricUnitEditor.GetGroup(line, line.Units[0].Id).Range);
    }

    [Theory]
    [InlineData("一緒", "いっしょ")]
    [InlineData("本", "ほん")]
    [InlineData("戦場ヶ原", "せんじょうがはら")]
    public void SoundsActuallyBelongingToKanjiAreNotStripped(string text, string reading)
    {
        var line = JapaneseAnnotationService.ApplyManualReading(
            new LyricImportService().Parse(text, ".txt", LyricLanguage.Japanese).Lines[0], reading);
        var group = Assert.Single(JapaneseRubyBuilder.Build(line, false));
        Assert.Equal(text, group.Text); Assert.Equal(reading, group.Annotation);
    }

    [Fact]
    public void WrittenKanaInsideASharedGroupAnchorsSeparateKanjiReadings()
    {
        var line = JapaneseAnnotationService.ApplyManualReading(
            new LyricImportService().Parse("取り戻す", ".txt", LyricLanguage.Japanese).Lines[0], "とりもどす");
        line = line with { Units = line.Units.Select(unit => unit with { DisplayRanges = [new TextRange(0, 4)] }).ToArray() };
        var display = JapaneseRubyBuilder.Build(line, false);
        Assert.Equal(new[] { "取", "り", "戻", "す" }, display.Select(group => group.Text));
        Assert.Equal(new[] { "と", "", "もど", "" }, display.Select(group => group.Annotation));
    }

    [Fact]
    public void KanaIsOnlyAboveKanjiAndRomajiReplacesItWithMappedGroups()
    {
        var line = MakeLine();
        var kana = JapaneseRubyBuilder.Build(line, false);
        Assert.Equal(new[] { "一緒", "に", "行", "き", "ま", "しょ", "う" }, kana.Select(group => group.Text));
        Assert.Equal(new[] { "いっしょ", "", "い", "", "", "", "" }, kana.Select(group => group.Annotation));
        var romaji = JapaneseRubyBuilder.Build(line, true);
        Assert.Equal(new[] { "is sho", "ni", "i", "ki", "ma", "sho", "u" }, romaji.Select(group => group.Annotation));
        Assert.Equal(new[] { 0, 1 }, kana[0].UnitIndexes);
        Assert.Equal(line.PrimaryText, string.Concat(kana.Select(group => group.Text)));
    }

    [Fact]
    public void MissingRomajiDoesNotBlockKanaBasedRecordingOrChangeTheUnits()
    {
        var line = MakeLine();
        line = line with { Units = line.Units.Select(unit => unit with { Romanization = null }).ToArray(), RomanizationText = null };
        Assert.Null(JapaneseAnnotationService.GetRecordingIssue(new(LyricLanguage.Japanese, [line])));
        Assert.Equal("is sho", JapaneseRubyBuilder.Build(line, true)[0].Annotation);
        Assert.All(line.Units, unit => Assert.Null(unit.Romanization));
    }

    [Fact]
    public void SplittingAtWordBoundaryKeepsIdsReadingsTimesAndCanMergeBack()
    {
        var line = MakeLine();
        var (left, right) = LyricLineEditor.SplitBeforeUnit(line, 3);
        Assert.Equal("一緒に", left.PrimaryText); Assert.Equal("行きましょう", right.PrimaryText);
        Assert.Equal(line.Units.Select(unit => unit.Id), left.Units.Concat(right.Units).Select(unit => unit.Id));
        Assert.Equal(line.Units.Select(unit => unit.Timing), left.Units.Concat(right.Units).Select(unit => unit.Timing));
        Assert.Equal(line.Units.Select(unit => unit.RawInputTime), left.Units.Concat(right.Units).Select(unit => unit.RawInputTime));
        Assert.Null(JapaneseAnnotationService.GetRecordingIssue(new(LyricLanguage.Japanese, [left, right])));
        var merged = LyricLineEditor.Merge(left, right);
        Assert.Equal(line.Units.Select(unit => unit.Id), merged.Units.Select(unit => unit.Id));
        Assert.Equal("行", merged.PrimaryText.Substring(merged.Units[3].DisplayRanges[0].Start, 1));
        Assert.Throws<InvalidOperationException>(() => LyricLineEditor.SplitBeforeUnit(line, 1));
        Assert.Throws<InvalidOperationException>(() => LyricLineEditor.SplitBeforeUnit(line, 0));
    }

    private static LyricLine MakeLine()
    {
        var source = new LyricImportService().Parse("一緒に行きましょう", ".txt", LyricLanguage.Japanese).Lines[0];
        string[] readings = ["いっ", "しょ", "に", "い", "き", "ま", "しょ", "う"];
        TextRange[] ranges = [new(0, 2), new(0, 2), new(2, 1), new(3, 1), new(4, 1), new(5, 1), new(6, 2), new(8, 1)];
        return source with { ReadingText = string.Concat(readings), ReadingSource = ReadingSource.Generated,
            Timing = new(ProjectTime.Zero, ProjectTime.FromSeconds(8)),
            Units = readings.Select((reading, index) => new LyricUnit(Guid.NewGuid(), [ranges[index]], reading,
                KanaRomanizer.RomanizeSingingUnit(reading, index + 1 < readings.Length ? readings[index + 1] : null),
                new(ProjectTime.FromSeconds(index), ProjectTime.FromSeconds(index + 1)), ProjectTime.FromSeconds(index), true)).ToArray() };
    }
}
