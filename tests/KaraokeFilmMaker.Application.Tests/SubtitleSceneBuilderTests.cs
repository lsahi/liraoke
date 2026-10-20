using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class SubtitleSceneBuilderTests
{
    private readonly SubtitleSceneBuilder _builder = new();

    [Fact]
    public void PunctuationSharesPrecedingWordSweepWithoutAddingSingingUnits()
    {
        LyricLine line = Line("\"La, la?!\"", 0, 10) with
        {
            Units = [Unit(new TextRange(1, 2), 2, 3), Unit(new TextRange(5, 2), 5, 7)],
        };
        string original = System.Text.Json.JsonSerializer.Serialize(line);
        Assert.Equal(0, PunctuationProgress(line, 0, 1.999999));
        Assert.Equal(0, PunctuationProgress(line, 0, 2));
        Assert.Equal(0.5, PunctuationProgress(line, 3, 2.5));
        Assert.Equal(1, PunctuationProgress(line, 3, 3));
        Assert.Equal(1, PunctuationProgress(line, 3, 5));
        Assert.Equal(0, _builder.BuildHighlights(line, ProjectTime.FromSeconds(5))
            .Single(highlight => highlight.DisplayRanges.Contains(new TextRange(5, 5))).Progress);
        Assert.Equal(0.5, _builder.BuildHighlights(line, ProjectTime.FromSeconds(6))
            .Single(highlight => highlight.DisplayRanges.Contains(new TextRange(5, 5))).Progress);
        foreach (int trailingIndex in new[] { 7, 8, 9 })
        {
            Assert.Equal(0.5, PunctuationProgress(line, trailingIndex, 6));
            Assert.Equal(1, PunctuationProgress(line, trailingIndex, 7));
        }
        Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(line));
        Assert.Equal(2, line.Units.Count);
    }

    [Theory]
    [InlineData("「你，好！」")]
    [InlineData("「あ、い！」")]
    [InlineData("\"a,b!?\"")]
    public void ChineseJapaneseAndEnglishPunctuationUseTheSameDisplayRule(string text)
    {
        LyricLine line = Line(text, 0, 10) with
        {
            Units = [Unit(new TextRange(1, 1), 2, 3), Unit(new TextRange(3, 1), 5, 7)],
        };
        Assert.Equal(0, PunctuationProgress(line, 0, 2));
        Assert.Equal(0.5, PunctuationProgress(line, 2, 2.5));
        Assert.Equal(1, PunctuationProgress(line, 2, 5));
        Assert.Equal(0.5, PunctuationProgress(line, 4, 6));
        Assert.Equal(1, PunctuationProgress(line, 4, 7));
        Assert.Equal(1, PunctuationProgress(line, 5, 7));
    }

    [Fact]
    public void SharedKanjiMappingAndUncoveredLettersDoNotChangePunctuationOwnership()
    {
        LyricLine line = Line("「未来」、先。x", 0, 10) with
        {
            Units = [Unit(new TextRange(1, 2), 2, 3), Unit(new TextRange(1, 2), 3, 4),
                Unit(new TextRange(5, 1), 6, 8)],
        };
        Assert.Equal(0, PunctuationProgress(line, 0, 2));
        Assert.Equal(0.5, PunctuationProgress(line, 3, 3));
        Assert.Equal(1, PunctuationProgress(line, 3, 4));
        Assert.Equal(1, PunctuationProgress(line, 3, 6));
        Assert.Equal(1, PunctuationProgress(line, 4, 6));
        Assert.Equal(1, PunctuationProgress(line, 6, 8));
        Assert.DoesNotContain(_builder.BuildHighlights(line, ProjectTime.FromSeconds(9)),
            highlight => highlight.DisplayRanges.Any(range => range.Start <= 7 && range.EndExclusive > 7));
    }

    [Fact]
    public void WordInternalApostrophesAndHyphensKeepContinuousWordSweep()
    {
        LyricLine line = Line("it's re-made", 0, 10) with
        {
            Units = [Unit(new TextRange(0, 4), 2, 4), Unit(new TextRange(5, 7), 5, 7)],
        };
        var highlights = _builder.BuildHighlights(line, ProjectTime.FromSeconds(3));
        Assert.Equal(2, highlights.Count);
        Assert.Equal(0.5, highlights[0].Progress);
        Assert.Equal(0, highlights[1].Progress);
    }

    [Fact]
    public void SupplementaryUnicodePunctuationUsesItsWholeUtf16Range()
    {
        LyricLine line = Line("a\U00010100b", 0, 10) with
        {
            Units = [Unit(new TextRange(0, 1), 2, 3), Unit(new TextRange(3, 1), 5, 7)],
        };
        var punctuation = Assert.Single(_builder.BuildHighlights(line, ProjectTime.FromSeconds(5)),
            highlight => highlight.DisplayRanges.Contains(new TextRange(0, 3)));
        Assert.Equal(1, punctuation.Progress);
    }

    [Fact]
    public void RetimingAnImmutableLineRebuildsPunctuationTiming()
    {
        LyricLine line = Line("a,b", 0, 10) with
        {
            Units = [Unit(new TextRange(0, 1), 2, 3), Unit(new TextRange(2, 1), 5, 7)],
        };
        Assert.Equal(1, PunctuationProgress(line, 1, 5));
        var changed = line with { Units = [line.Units[0] with
            { Timing = new TimeRange(ProjectTime.FromSeconds(5), ProjectTime.FromSeconds(6)) }, line.Units[1]] };
        Assert.Equal(0, PunctuationProgress(changed, 1, 5));
        Assert.Equal(1, PunctuationProgress(changed, 1, 6));
    }

    [Fact]
    public void PunctuationDoesNotCreateHighlightForDisabledOrNonSingingLine()
    {
        Assert.Empty(_builder.BuildHighlights(Line("……？！", 0, 10), ProjectTime.FromSeconds(5)));
        var disabled = Line("a!", 0, 10) with
        {
            IsHighlightEnabled = false,
            Units = [Unit(new TextRange(0, 1), 2, 3)],
        };
        Assert.Empty(_builder.BuildHighlights(disabled, ProjectTime.FromSeconds(5)));
    }

    private double PunctuationProgress(LyricLine line, int index, double seconds) =>
        _builder.BuildHighlights(line, ProjectTime.FromSeconds(seconds))
            .Where(highlight => highlight.DisplayRanges.Any(range => range.Start <= index && range.EndExclusive > index))
            .Select(highlight => highlight.Progress).DefaultIfEmpty().Max();

    [Fact]
    public void SharedKanjiMapping_AdvancesContinuouslyAcrossUnequalMoraDurations()
    {
        LyricLine line = Line("未来", 0, 8) with
        {
            Units = [Unit(new TextRange(0, 2), 0, 1), Unit(new TextRange(0, 2), 1, 4), Unit(new TextRange(0, 2), 4, 8)],
        };

        SubtitleHighlight highlight = Assert.Single(_builder.BuildHighlights(line, ProjectTime.FromSeconds(2.5)));
        Assert.Equal(0.5, highlight.Progress, precision: 8);
        Assert.Equal(new TextRange(0, 2), Assert.Single(highlight.DisplayRanges));
    }

    [Fact]
    public void AuxiliaryProgressUsesSingingUnitsRatherThanNumberOfKanjiGroups()
    {
        LyricLine line = Line("未来へ", 0, 4) with
        {
            Units = [Unit(new TextRange(0, 2), 0, 1), Unit(new TextRange(0, 2), 1, 2),
                Unit(new TextRange(0, 2), 2, 3), Unit(new TextRange(2, 1), 3, 4)],
        };
        SubtitleLineScene scene = Assert.Single(_builder.Build(
            new LyricDocument(LyricLanguage.Japanese, [line]), ProjectTime.FromSeconds(2)).Lines);
        Assert.Equal(0.5, scene.AuxiliaryProgress, precision: 8);
    }

    [Fact]
    public void NextLineBecomesActiveInItsExistingSlotAtExactBoundary()
    {
        LyricLine first = Line("第一句", 0, 2);
        LyricLine second = Line("第二句", 2, 4);
        LyricLine third = Line("第三句", 4, 6);
        var document = new LyricDocument(LyricLanguage.Chinese, [first, second, third]);
        SubtitleScene before = _builder.Build(document, ProjectTime.FromSeconds(1));
        SubtitleScene boundary = _builder.Build(document, ProjectTime.FromSeconds(2));

        Assert.Equal(SubtitleSlot.BottomRight, before.Lines[1].Slot);
        SubtitleLineScene active = Assert.Single(boundary.Lines, line => line.IsActive);
        Assert.Equal(second.Id, active.Line.Id);
        Assert.Equal(SubtitleSlot.BottomRight, active.Slot);
        Assert.Equal(SubtitleSlot.TopLeft, boundary.Lines[1].Slot);
        Assert.All(boundary.Lines[1].Highlights, span => Assert.Equal(0, span.Progress));
    }

    [Fact]
    public void GapDoesNotKeepExpiredLineVisible()
    {
        LyricLine first = Line("结束", 0, 1);
        LyricLine second = Line("将开始", 3, 4);
        SubtitleScene scene = _builder.Build(new LyricDocument(LyricLanguage.Chinese, [first, second]),
            ProjectTime.FromSeconds(2));

        SubtitleLineScene next = Assert.Single(scene.Lines);
        Assert.Equal(second.Id, next.Line.Id);
        Assert.False(next.IsActive);
        Assert.Empty(next.Highlights);
    }

    [Fact]
    public void DisabledHighlightKeepsTimedLineVisibleWithoutSweeping()
    {
        LyricLine line = Line("只显示", 0, 4) with { IsHighlightEnabled = false };
        SubtitleLineScene scene = Assert.Single(_builder.Build(
            new LyricDocument(LyricLanguage.Chinese, [line]), ProjectTime.FromSeconds(2)).Lines);
        Assert.True(scene.IsActive);
        Assert.Empty(scene.Highlights);
    }

    [Fact]
    public void UntimedAndInstrumentalRowsDoNotConsumeAlternatingSlots()
    {
        LyricLine line = Line("有效行", 2, 4);
        SubtitleScene scene = _builder.Build(new LyricDocument(LyricLanguage.Japanese,
            [Line("无时间", 0, 0), Line("间奏", 0, 2) with { IsInstrumental = true }, line]),
            ProjectTime.FromSeconds(2));
        Assert.Equal(SubtitleSlot.TopLeft, Assert.Single(scene.Lines).Slot);
    }

    private static LyricLine Line(string text, double start, double end) => new(
        Guid.NewGuid(), text, AnnotationType.None, null, null,
        new TimeRange(ProjectTime.FromSeconds(start), ProjectTime.FromSeconds(end)), [], ReviewState.Unreviewed, null);

    private static LyricUnit Unit(TextRange range, double start, double end) => new(
        Guid.NewGuid(), [range], "み", null,
        new TimeRange(ProjectTime.FromSeconds(start), ProjectTime.FromSeconds(end)), null, false);
}
