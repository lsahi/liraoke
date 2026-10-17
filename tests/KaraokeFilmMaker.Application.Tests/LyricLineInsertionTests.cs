using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Validation;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class LyricLineInsertionTests
{
    [Theory]
    [InlineData(1, 3, 3, 5, true)]
    [InlineData(0, 3, 3, 5, true)]
    [InlineData(1, 4, 3, 5, true)]
    [InlineData(1, 3, 4, 5, false)]
    [InlineData(0, 0, 0, 0, false)]
    [InlineData(1, 3, 0, 0, false)]
    [InlineData(0, 0, 1, 3, false)]
    [InlineData(1, 3, 3, 3, false)]
    public void RequiresConfirmationOnlyWhereTimedNeighborsLeaveNoGap(
        double start1, double end1, double start2, double end2, bool expected)
    {
        LyricLine[] lines = [Line("前句", start1, end1), Line("后句", start2, end2)];
        Assert.Equal(expected, LyricLineInsertion.RequiresTimingReset(lines, 1));
    }

    [Fact]
    public void ImportedUnconfirmedTimingStillRequiresConfirmation()
    {
        var document = new LyricImportService().Parse("[00:01]前句\n[00:03]后句", ".lrc", LyricLanguage.Chinese);
        Assert.All(document.Lines.SelectMany(line => line.Units), unit => Assert.False(unit.IsConfirmed));
        Assert.True(LyricLineInsertion.RequiresTimingReset(document.Lines, 1));
    }

    [Fact]
    public void NoConfirmationIsNeededBeforeFirstOrAfterLastSentence()
    {
        LyricLine[] lines = [Line("前句", 1, 3), Line("后句", 3, 5)];
        Assert.False(LyricLineInsertion.RequiresTimingReset(lines, 0));
        Assert.False(LyricLineInsertion.RequiresTimingReset(lines, lines.Length));
        Assert.False(LyricLineInsertion.RequiresTimingReset([], 0));
    }

    [Fact]
    public void RefusingRequiredResetLeavesEverySourceRecordUntouched()
    {
        LyricLine previous = Line("前句", 1, 3, confirmed: true);
        LyricLine next = Line("后句", 3, 5, confirmed: true);
        LyricLine[] lines = [previous, next];
        LyricLine inserted = Line("新句", 20, 25, confirmed: true);

        Assert.Throws<InvalidOperationException>(() => LyricLineInsertion.Insert(lines, 1, inserted));

        Assert.Same(previous, lines[0]);
        Assert.Same(next, lines[1]);
        Assert.All(next.Units, unit => Assert.True(unit.IsConfirmed));
        Assert.All(inserted.Units, unit => Assert.True(unit.IsConfirmed));
        Assert.Equal(ProjectTime.FromSeconds(25), inserted.Timing.End);
    }

    [Fact]
    public void ConfirmedInsertionClearsAllFollowingTimesButPreservesEarlierTimingAndLanguageData()
    {
        LyricLine previous = Line("前句", 1, 3, confirmed: true);
        LyricLine following = WithPresentation(Line("后句", 3, 5, confirmed: true));
        LyricLine last = WithPresentation(Line("尾句", 8, 10, confirmed: true)) with
        { IsInstrumental = true, IsHighlightEnabled = false };
        LyricLine inserted = WithPresentation(Line("新句", 20, 25, confirmed: true));
        LyricLine[] original = [previous, following, last];

        var result = LyricLineInsertion.Insert(original, 1, inserted, resetFollowing: true);

        Assert.Equal(4, result.Count);
        Assert.Same(previous, result[0]);
        Assert.Equal(ProjectTime.FromSeconds(3), result[0].Timing.End);
        AssertCleared(inserted, result[1]);
        AssertCleared(following, result[2]);
        AssertCleared(last, result[3]);
        Assert.Same(following, original[1]);
        Assert.Same(last, original[2]);
        Assert.Equal(ProjectTime.FromSeconds(5), following.Timing.End);
        Assert.All(following.Units, unit => Assert.True(unit.IsConfirmed));
    }

    [Fact]
    public void InsertingIntoRealGapPreservesAllExistingRecords()
    {
        LyricLine previous = Line("前句", 1, 3, confirmed: true);
        LyricLine next = Line("后句", 5, 8, confirmed: true);
        LyricLine inserted = Line("新句", 15, 18, confirmed: true);

        var result = LyricLineInsertion.Insert([previous, next], 1, inserted);

        Assert.Same(previous, result[0]);
        Assert.Same(next, result[2]);
        AssertCleared(inserted, result[1]);
    }

    [Fact]
    public void CanInsertAtEitherBoundaryAndIntoAnEmptyDocument()
    {
        LyricLine existing = Line("已有", 1, 3, confirmed: true);
        LyricLine inserted = Line("新增", 0, 0);
        Assert.Same(existing, LyricLineInsertion.Insert([existing], 0, inserted)[1]);
        Assert.Same(existing, LyricLineInsertion.Insert([existing], 1, inserted)[0]);
        var single = Assert.Single(LyricLineInsertion.Insert([], 0, inserted));
        Assert.Equal(inserted.Id, single.Id);
        Assert.Equal(ProjectTime.Zero, single.Timing.End);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void InvalidInsertionIndexIsRejected(int index)
    {
        LyricLine[] lines = [Line("已有", 1, 3)];
        Assert.Throws<ArgumentOutOfRangeException>(() => LyricLineInsertion.RequiresTimingReset(lines, index));
        Assert.Throws<ArgumentOutOfRangeException>(() => LyricLineInsertion.Insert(lines, index, Line("新增", 0, 0)));
    }

    [Fact]
    public void ReusingExistingLineIdentityIsRejected()
    {
        LyricLine existing = Line("已有", 1, 3);
        Assert.Throws<ArgumentException>(() => LyricLineInsertion.Insert([existing], 1, existing));
    }

    [Fact]
    public void ResetLinesDisappearFromSubtitleTimelineAndReturnToTapPlan()
    {
        LyricLine previous = WithPresentation(Line("前句", 1, 3, confirmed: true));
        LyricLine next = WithPresentation(Line("后句", 3, 5, confirmed: true));
        LyricLine inserted = WithPresentation(Line("新句", 0, 0));
        var lines = LyricLineInsertion.Insert([previous, next], 1, inserted, resetFollowing: true);
        var document = new LyricDocument(LyricLanguage.Chinese, lines);

        LyricRecordingProgress progress = LyricRecordingProgress.From(document);
        Assert.Equal(1, progress.FirstIncompleteLineIndex);
        Assert.Equal(1, progress.CompletedLines);
        Assert.Equal(previous.Units.Count, progress.ConfirmedUnits);
        var plan = UnitTapPlan.Build(document, 1, onlyUnconfirmed: true);
        Assert.Equal(inserted.Id, plan[0].LineId);
        Assert.Equal(inserted.Units.Count + next.Units.Count, plan.Count);
        Assert.Empty(new SubtitleSceneBuilder().Build(document, ProjectTime.FromSeconds(4)).Lines);
        Assert.Equal(previous.Id, Assert.Single(new SubtitleSceneBuilder()
            .Build(document, ProjectTime.FromSeconds(2)).Lines).Line.Id);
    }

    [Fact]
    public void ClearedPresentationDataStillPassesProjectValidation()
    {
        LyricLine previous = WithPresentation(Line("前句", 1, 3, confirmed: true));
        LyricLine next = WithPresentation(Line("后句", 3, 5, confirmed: true));
        var lines = LyricLineInsertion.Insert([previous, next], 1,
            WithPresentation(Line("新句", 0, 0)), resetFollowing: true);
        var project = KaraokeProject.Create("插入歌词测试", DateTimeOffset.UtcNow) with
        { Lyrics = new LyricDocument(LyricLanguage.Chinese, lines) };

        Assert.Empty(ProjectValidator.Validate(project));
    }

    private static LyricLine Line(string text, double start, double end, bool confirmed = false)
    {
        LyricLine line = new LyricImportService().Parse(text, ".txt", LyricLanguage.Chinese).Lines[0];
        line = LyricTimingService.Retiming(line, new(ProjectTime.FromSeconds(start), ProjectTime.FromSeconds(end)));
        return confirmed ? line with
        {
            ReviewState = ReviewState.Confirmed,
            Units = line.Units.Select(unit => unit with { IsConfirmed = true, RawInputTime = unit.Timing.Start }).ToArray(),
        } : line;
    }

    private static LyricLine WithPresentation(LyricLine line) => line with
    {
        ReadingSource = ReadingSource.Manual,
        ReadingText = "读音",
        RomanizationText = "reading",
        TranslationText = "translation",
        AnnotationType = AnnotationType.Pinyin,
        AnnotationText = "annotation",
        LanguageSpans = [new(Guid.NewGuid(), new(0, line.PrimaryText.Length), LanguageIds.Chinese,
            LanguageDetectionSource.Manual, IsLocked: true)],
        Presentation = new(line.PrimaryText,
            line.Units.Select(unit => new LyricRecordingCell(unit.Id, unit.Reading)).ToArray(),
            line.Units.Select(unit => new LyricSubtitleRun(unit.DisplayRanges[0], "读音", null, [unit.Id], LanguageIds.Chinese)).ToArray()),
    };

    private static void AssertCleared(LyricLine source, LyricLine result)
    {
        Assert.Equal(new TimeRange(ProjectTime.Zero, ProjectTime.Zero), result.Timing);
        Assert.Equal(source.ReviewState, result.ReviewState);
        Assert.Equal(source.Id, result.Id);
        Assert.Equal(source.PrimaryText, result.PrimaryText);
        Assert.Equal(source.ReadingText, result.ReadingText);
        Assert.Equal(source.RomanizationText, result.RomanizationText);
        Assert.Equal(source.TranslationText, result.TranslationText);
        Assert.Equal(source.AnnotationType, result.AnnotationType);
        Assert.Equal(source.AnnotationText, result.AnnotationText);
        Assert.Equal(source.ReadingSource, result.ReadingSource);
        Assert.Equal(source.IsInstrumental, result.IsInstrumental);
        Assert.Equal(source.IsHighlightEnabled, result.IsHighlightEnabled);
        Assert.Same(source.LanguageSpans, result.LanguageSpans);
        Assert.Same(source.Presentation, result.Presentation);
        Assert.Equal(source.Units.Count, result.Units.Count);
        for (int index = 0; index < source.Units.Count; index++)
        {
            LyricUnit unit = result.Units[index];
            Assert.Equal(new TimeRange(ProjectTime.Zero, ProjectTime.Zero), unit.Timing);
            Assert.False(unit.IsConfirmed);
            Assert.Null(unit.RawInputTime);
            Assert.Equal(source.Units[index].Id, unit.Id);
            Assert.Same(source.Units[index].DisplayRanges, unit.DisplayRanges);
            Assert.Equal(source.Units[index].Reading, unit.Reading);
            Assert.Equal(source.Units[index].Romanization, unit.Romanization);
            Assert.Equal(source.Units[index].LanguageId, unit.LanguageId);
        }
    }
}
