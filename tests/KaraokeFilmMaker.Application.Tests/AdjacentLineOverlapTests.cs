using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class AdjacentLineOverlapTests
{
    private static ProjectTime At(double s) => ProjectTime.FromSeconds(s);
    private readonly LyricTimingService _service = new();
    private LyricDocument Record(LyricDocument doc, int line, double first, double second, double end,
        bool explicitEnd = true, bool remaining = true) =>
        _service.ApplyContinuousUnitMarkers(doc, [At(first), At(second)], At(100), line, At(end),
            [At(first + .01), At(second + .01)], onlyUnconfirmed: remaining, explicitLineEnd: explicitEnd);
    private static LyricDocument Lyrics() => new LyricImportService().Parse("春风\n明天\n再唱", ".txt", LyricLanguage.Chinese);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitPreviousTailAllowsOverlapWithoutChangingSavedUnits(bool remaining)
    {
        var first = Record(Lyrics(), 0, 1, 2, 8);
        var result = Record(first, 1, 6, 7, 10, remaining: remaining);
        Assert.Same(first.Lines[0], result.Lines[0]);
        Assert.Equal(At(8), result.Lines[0].ExplicitEndTime);
        Assert.Equal(At(10), result.Lines[1].ExplicitEndTime);
        Assert.Equal(At(6), result.Lines[1].Timing.Start);
        Assert.Equal(At(6.01), result.Lines[1].Units[0].RawInputTime);
        Assert.Throws<ArgumentException>(() => Record(result, 2, 7.5, 9, 11, remaining: remaining));
        Assert.Equal(At(8), Record(result, 2, 8, 9, 11, remaining: remaining).Lines[2].Timing.Start);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InferredOrStaleEndCannotAuthorizeOverlap(bool remaining)
    {
        var first = Record(Lyrics(), 0, 1, 2, 8, explicitEnd: false);
        Assert.Throws<ArgumentException>(() => Record(first, 1, 6, 7, 10, remaining: remaining));
        first = first with { Lines = first.Lines.Select((l, i) => i == 0 ? l with { ExplicitEndTime = At(7) } : l).ToArray() };
        Assert.Throws<ArgumentException>(() => Record(first, 1, 6, 7, 10, remaining: remaining));
    }

    [Fact]
    public void ContinuousBatchOnlyMarksItsFinalRowAsExplicit()
    {
        var doc = _service.ApplyContinuousUnitMarkers(Lyrics(), [At(1), At(2), At(3), At(4)], At(100), 0, At(8),
            explicitLineEnd: true);
        Assert.Null(doc.Lines[0].ExplicitEndTime);
        Assert.Equal(At(8), doc.Lines[1].ExplicitEndTime);
        Assert.Throws<ArgumentException>(() => Record(doc, 1, 2.5, 4, 8, remaining: false));
        Assert.Throws<ArgumentException>(() => Record(doc, 2, 6, 6, 10));
        Assert.Throws<ArgumentException>(() => Record(doc, 2, 6, 5, 10));
        Assert.Equal(At(6), Record(doc, 2, 6, 7, 10).Lines[2].Timing.Start);
    }

    [Fact]
    public void ReRecordingPreviousLineMustNotCreateThirdConcurrentRow()
    {
        var doc = Record(Record(Record(Lyrics(), 0, 1, 2, 4), 1, 4, 5, 8), 2, 8, 9, 10);
        Assert.Throws<ArgumentException>(() => Record(doc, 0, 1, 2, 9, remaining: false));
        Assert.Equal(At(6), Record(doc, 0, 1, 2, 6, remaining: false).Lines[0].Timing.End);
    }

    [Fact]
    public void SplitResetOffsetAndInsertionHandleExplicitEnds()
    {
        var line = Record(Lyrics(), 0, 1, 2, 8).Lines[0];
        var split = LyricLineEditor.SplitBeforeUnit(line, 1);
        Assert.Null(split.Left.ExplicitEndTime);
        Assert.Equal(line.ExplicitEndTime, split.Right.ExplicitEndTime);
        Assert.Equal(At(9), LyricLineEditor.Offset(line, 1_000_000).ExplicitEndTime);
        Assert.Null(LyricTimingService.Retiming(line, new(At(1), At(9))).ExplicitEndTime);
        Assert.All(LyricTimingService.ClearAllTimings(new(LyricLanguage.Chinese, [line])).Lines,
            l => Assert.Null(l.ExplicitEndTime));
        Assert.Null(LyricLineInsertion.Insert([], 0, line)[0].ExplicitEndTime);
    }

    [Fact]
    public void BothActiveRowsSweepIndependentlyAndKeepSlotsAcrossSeekAndBoundaries()
    {
        var doc = Record(Record(Record(Lyrics(), 0, 1, 2, 8), 1, 6, 7, 10), 2, 8, 9, 11);
        var builder = new SubtitleSceneBuilder();
        var overlap = builder.Build(doc, At(7.5));
        Assert.Equal(new[] { doc.Lines[0].Id, doc.Lines[1].Id }, overlap.Lines.Select(l => l.Line.Id));
        Assert.All(overlap.Lines, l => Assert.True(l.IsActive));
        Assert.Equal(new[] { SubtitleSlot.TopLeft, SubtitleSlot.BottomRight }, overlap.Lines.Select(l => l.Slot));
        Assert.InRange(overlap.Lines[0].Highlights[1].Progress, .91, .92);
        Assert.InRange(overlap.Lines[1].Highlights[1].Progress, .16, .17);
        var boundary = builder.Build(doc, At(8));
        Assert.Equal(new[] { doc.Lines[1].Id, doc.Lines[2].Id }, boundary.Lines.Select(l => l.Line.Id));
        Assert.Equal(SubtitleSlot.BottomRight, boundary.Lines[0].Slot);
        Assert.Equal(overlap.Lines.Select(l => l.AuxiliaryProgress), builder.Build(doc, At(7.5)).Lines.Select(l => l.AuxiliaryProgress));
        Assert.Empty(builder.Build(doc, At(11)).Lines);
    }

    [Fact]
    public void ShortFollowingRowDoesNotRemoveTheLongPreviousTailOrShowThirdInItsSlot()
    {
        var doc = Record(Record(Record(Lyrics(), 0, 1, 2, 10), 1, 6, 7, 8), 2, 10, 11, 12);
        var builder = new SubtitleSceneBuilder();
        Assert.Equal(2, builder.Build(doc, At(7.5)).Lines.Count);
        var tail = Assert.Single(builder.Build(doc, At(9)).Lines);
        Assert.Equal(doc.Lines[0].Id, tail.Line.Id); Assert.True(tail.IsActive);
        Assert.Equal(doc.Lines[2].Id, Assert.Single(builder.Build(doc, At(10)).Lines).Line.Id);
    }

    [Fact]
    public void ExplicitLineEndRemainsTheBoundaryWhenFineEditingShortensTheLastUnit()
    {
        var first = Record(Lyrics(), 0, 1, 2, 10);
        var line = first.Lines[0] with { Units = first.Lines[0].Units.Select((u, i) =>
            i == 1 ? u with { Timing = new(At(2), At(5)) } : u).ToArray() };
        var doc = first with { Lines = [line, first.Lines[1], first.Lines[2]] };
        doc = Record(doc, 1, 6, 7, 8);
        Assert.Throws<ArgumentException>(() => Record(doc, 2, 9, 10, 11));
        Assert.Equal(At(10), Record(doc, 2, 10, 11, 12).Lines[2].Timing.Start);
    }

    [Fact]
    public void EnterCannotBeSilentlyShortenedToAvoidANonAdjacentSavedRow()
    {
        var doc = Record(Lyrics(), 2, 8, 9, 10);
        Assert.Throws<ArgumentException>(() => Record(doc, 0, 1, 2, 12));
        Assert.Equal(At(6), Record(doc, 0, 1, 2, 6).Lines[0].ExplicitEndTime);
    }
}
