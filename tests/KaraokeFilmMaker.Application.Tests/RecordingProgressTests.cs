using System.Text.Json;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class RecordingProgressTests
{
    private readonly LyricTimingService _service = new();
    private static ProjectTime At(double seconds) => ProjectTime.FromSeconds(seconds);
    private static LyricDocument Lyrics(string text) => new LyricImportService().Parse(text, ".txt", LyricLanguage.Chinese);

    private static LyricDocument Confirm(LyricDocument document, int lineIndex, int unitIndex, double start, double end)
    {
        var lines = document.Lines.ToArray();
        var line = lines[lineIndex];
        var units = line.Units.ToArray();
        units[unitIndex] = units[unitIndex] with
        {
            IsConfirmed = true,
            Timing = new TimeRange(At(start), At(end)),
            RawInputTime = At(start + 0.125),
        };
        lines[lineIndex] = line with { Units = units };
        return document with { Lines = lines };
    }

    [Fact]
    public void ImportedLineTimesAndRawInputsAreNotConfirmedProgress()
    {
        var imported = new LyricImportService().Parse("[00:01]春风\n[00:04]明天", ".lrc", LyricLanguage.Chinese);
        imported = imported with { Lines = imported.Lines.Select(line => line with
        {
            ReviewState = ReviewState.Confirmed,
            Units = line.Units.Select(unit => unit with { RawInputTime = unit.Timing.Start }).ToArray(),
        }).ToArray() };
        var progress = LyricRecordingProgress.From(imported);
        Assert.Equal(4, progress.TotalUnits);
        Assert.Equal(0, progress.ConfirmedUnits);
        Assert.Equal(0, progress.CompletedLines);
        Assert.Equal(2, progress.TotalLines);
        Assert.Equal(0, progress.FirstIncompleteLineIndex);
        Assert.False(progress.IsComplete);
        Assert.Equal(4, UnitTapPlan.Build(imported, 0, onlyUnconfirmed: true).Count);
    }

    [Fact]
    public void SavedCompletedUnitsRestoreProgressAndPlanWithoutChangingSavedData()
    {
        var document = Confirm(Confirm(Lyrics("春风\n明天"), 0, 0, 1, 2), 0, 1, 2, 3);
        document = Confirm(document, 1, 0, 4, 5);
        string json = JsonSerializer.Serialize(document);
        var restored = JsonSerializer.Deserialize<LyricDocument>(json)!;
        var progress = LyricRecordingProgress.From(restored);
        Assert.Equal(3, progress.ConfirmedUnits);
        Assert.Equal(1, progress.CompletedLines);
        Assert.Equal(1, progress.FirstIncompleteLineIndex);
        var target = Assert.Single(UnitTapPlan.Build(restored, 0, onlyUnconfirmed: true));
        Assert.Equal("天", target.DisplayText);
        Assert.Equal(1, target.LineIndex);
        Assert.Equal(1, target.UnitIndex);
        Assert.Equal(json, JsonSerializer.Serialize(restored));
    }

    [Fact]
    public void EmptyAndDisplayOnlyDocumentsAreNotReportedAsCompleted()
    {
        var document = Lyrics("间奏\n显示\n空白");
        document = document with { Lines = document.Lines.Select((line, index) => index switch
        {
            0 => line with { IsInstrumental = true },
            1 => line with { IsHighlightEnabled = false },
            _ => line with { Units = [] },
        }).ToArray() };
        var progress = LyricRecordingProgress.From(document);
        Assert.Equal(0, progress.TotalUnits);
        Assert.Equal(0, progress.TotalLines);
        Assert.Null(progress.FirstIncompleteLineIndex);
        Assert.False(progress.IsComplete);
        Assert.Empty(UnitTapPlan.Build(document, 0, onlyUnconfirmed: true));
        Assert.False(LyricRecordingProgress.From(document with { Lines = [] }).IsComplete);
    }

    [Fact]
    public void InvalidZeroDurationConfirmationStillNeedsRecording()
    {
        var document = Confirm(Lyrics("春"), 0, 0, 1, 1);
        Assert.False(LyricRecordingProgress.IsCompleted(document.Lines[0].Units[0]));
        Assert.Equal(0, LyricRecordingProgress.From(document).ConfirmedUnits);
        Assert.Single(UnitTapPlan.Build(document, 0, onlyUnconfirmed: true));
        var result = _service.ApplyContinuousUnitMarkers(document, [At(2)], At(10), 0, At(3), onlyUnconfirmed: true);
        Assert.True(LyricRecordingProgress.From(result).IsComplete);
    }

    [Fact]
    public void FullyCompletedDocumentHasNoContinuationTargetsButSupportsExplicitRerecording()
    {
        var document = Confirm(Confirm(Lyrics("春风"), 0, 0, 1, 2), 0, 1, 2, 3);
        Assert.True(LyricRecordingProgress.From(document).IsComplete);
        Assert.Null(LyricRecordingProgress.From(document).FirstIncompleteLineIndex);
        Assert.Empty(UnitTapPlan.Build(document, 0, onlyUnconfirmed: true));
        Assert.Equal(2, UnitTapPlan.Build(document, 0).Count);
        Assert.Throws<ArgumentException>(() => _service.ApplyContinuousUnitMarkers(document, [At(4)], At(10), 0, At(6), onlyUnconfirmed: true));
        var rerecorded = _service.ApplyContinuousUnitMarkers(document, [At(4), At(5)], At(10), 0, At(6));
        Assert.Equal(At(4), rerecorded.Lines[0].Units[0].Timing.Start);
        Assert.Equal(At(6), rerecorded.Lines[0].Units[1].Timing.End);
    }

    [Fact]
    public void ContinuationFillsOnlyRemainingUnitsAndPreservesConfirmedRawTimes()
    {
        var document = Confirm(Confirm(Lyrics("春风明天"), 0, 0, 1, 2), 0, 2, 4, 5);
        var result = _service.ApplyContinuousUnitMarkers(document, [At(2), At(5)], At(10), 0, At(7),
            [At(2.125), At(5.125)], onlyUnconfirmed: true);
        Assert.Same(document.Lines[0].Units[0], result.Lines[0].Units[0]);
        Assert.Same(document.Lines[0].Units[2], result.Lines[0].Units[2]);
        Assert.Equal(new TimeRange(At(2), At(4)), result.Lines[0].Units[1].Timing);
        Assert.Equal(new TimeRange(At(5), At(7)), result.Lines[0].Units[3].Timing);
        Assert.Equal(At(2.125), result.Lines[0].Units[1].RawInputTime);
        Assert.Equal(new TimeRange(At(1), At(7)), result.Lines[0].Timing);
        Assert.True(LyricRecordingProgress.From(result).IsComplete);
        Assert.Equal(ReviewState.Confirmed, result.Lines[0].ReviewState);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(9)]
    public void ContinuationPreservesConfirmedTailAndUsesItsEntryAsNewUnitEnd(double recordingEnd)
    {
        var document = Confirm(Lyrics("春风"), 0, 1, 4, 8);
        var result = _service.ApplyContinuousUnitMarkers(document, [At(2)], At(10), 0, At(recordingEnd), onlyUnconfirmed: true);
        Assert.Same(document.Lines[0].Units[1], result.Lines[0].Units[1]);
        Assert.Equal(new TimeRange(At(2), At(4)), result.Lines[0].Units[0].Timing);
        Assert.Equal(new TimeRange(At(2), At(8)), result.Lines[0].Timing);
    }

    [Fact]
    public void ContinuationSkipsCompletedLinesWithoutExtendingEarlierWordsAcrossThem()
    {
        var document = Confirm(Confirm(Lyrics("春风\n明天\n再唱"), 1, 0, 4, 5), 1, 1, 5, 6);
        var result = _service.ApplyContinuousUnitMarkers(document, [At(1), At(2), At(7), At(8)], At(12), 0, At(9), onlyUnconfirmed: true);
        Assert.Same(document.Lines[1], result.Lines[1]);
        Assert.Equal(new TimeRange(At(1), At(4)), result.Lines[0].Timing);
        Assert.Equal(new TimeRange(At(7), At(9)), result.Lines[2].Timing);
        Assert.True(LyricRecordingProgress.From(result).IsComplete);
    }

    [Theory]
    [InlineData(1.5, 5)]
    [InlineData(2, 4.5)]
    [InlineData(4, 5)]
    public void ContinuationRejectsOverlapWithConfirmedUnits(double first, double second)
    {
        var document = Confirm(Confirm(Lyrics("春风明天"), 0, 0, 1, 2), 0, 2, 4, 5);
        string before = JsonSerializer.Serialize(document);
        Assert.Throws<ArgumentException>(() => _service.ApplyContinuousUnitMarkers(document,
            [At(first), At(second)], At(10), 0, At(7), onlyUnconfirmed: true));
        Assert.Equal(before, JsonSerializer.Serialize(document));
    }

    [Fact]
    public void PartialSentenceRequiresEveryRemainingUnitBeforeApplying()
    {
        var document = Confirm(Lyrics("春风明天\n再唱"), 0, 1, 3, 4);
        Assert.Throws<ArgumentException>(() => _service.ApplyContinuousUnitMarkers(document,
            [At(1), At(4)], At(12), 0, At(7), onlyUnconfirmed: true));
        var result = _service.ApplyContinuousUnitMarkers(document, [At(1), At(4), At(5)], At(12), 0, At(7), onlyUnconfirmed: true);
        Assert.Same(document.Lines[0].Units[1], result.Lines[0].Units[1]);
        Assert.Same(document.Lines[1], result.Lines[1]);
        Assert.Equal(1, LyricRecordingProgress.From(result).FirstIncompleteLineIndex);
    }

    [Fact]
    public void ContinuationAcceptsExplicitFinalMarkerAndPreservesEarlierCompletedLines()
    {
        var document = Confirm(Confirm(Lyrics("春风\n明天"), 0, 0, 1, 2), 0, 1, 2, 3);
        var result = _service.ApplyContinuousUnitMarkers(document, [At(4), At(5), At(6)], At(10), 1, At(9),
            [At(4.1), At(5.1), At(6.1)], onlyUnconfirmed: true);
        Assert.Same(document.Lines[0], result.Lines[0]);
        Assert.Equal(At(6), result.Lines[1].Timing.End);
        Assert.Equal(At(5.1), result.Lines[1].Units[1].RawInputTime);
    }
}
