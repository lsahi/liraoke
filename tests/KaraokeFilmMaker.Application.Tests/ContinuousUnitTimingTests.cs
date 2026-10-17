using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class ContinuousUnitTimingTests
{
    private readonly LyricTimingService _service = new();
    private static ProjectTime At(double seconds) => ProjectTime.FromSeconds(seconds);
    private static LyricDocument Lyrics() => new LyricImportService().Parse("春风\n明天", ".txt", LyricLanguage.Chinese);

    [Fact]
    public void UntimedLyricsUseOneTapPerCharacterAndAutomaticallyCrossLines()
    {
        var document = Lyrics();
        var plan = UnitTapPlan.Build(document, 0);
        Assert.Equal(new[] { "春", "风", "明", "天" }, plan.Select(item => item.DisplayText));
        var result = _service.ApplyContinuousUnitMarkers(document, [At(1), At(2), At(4), At(5)], At(10), 0, At(7));
        Assert.Equal(new TimeRange(At(1), At(4)), result.Lines[0].Timing);
        Assert.Equal(new TimeRange(At(4), At(7)), result.Lines[1].Timing);
        Assert.Equal(new[] { 1d, 2, 4, 5 }, result.Lines.SelectMany(line => line.Units).Select(unit => unit.Timing.Start.TotalSeconds));
        Assert.Equal(new[] { 2d, 4, 5, 7 }, result.Lines.SelectMany(line => line.Units).Select(unit => unit.Timing.End.TotalSeconds));
        Assert.All(result.Lines.SelectMany(line => line.Units), unit => Assert.True(unit.IsConfirmed));
    }

    [Fact]
    public void FinishingACompleteSentenceLeavesUnrecordedLinesUntouched()
    {
        var document = Lyrics();
        var result = _service.ApplyContinuousUnitMarkers(document, [At(1), At(2)], At(10), 0, At(3));
        Assert.Equal(3, result.Lines[0].Timing.End.TotalSeconds);
        Assert.Equal(document.Lines[1], result.Lines[1]);
        var next = _service.ApplyContinuousUnitMarkers(result, [At(5), At(6)], At(10), 1, At(8));
        Assert.Equal(result.Lines[0], next.Lines[0]);
        Assert.Equal(5, next.Lines[1].Timing.Start.TotalSeconds);
    }

    [Fact]
    public void EndingHalfwayThroughSentenceDoesNotInventMissingWordTimes()
    {
        Assert.Throws<ArgumentException>(() => _service.ApplyContinuousUnitMarkers(Lyrics(), [At(1)], At(10), 0, At(3)));
        Assert.Throws<ArgumentException>(() => _service.ApplyContinuousUnitMarkers(Lyrics(), [At(1), At(2), At(3)], At(10), 0, At(5)));
    }

    [Fact]
    public void ExtraFinalTapRecordsLastWordEndAndCompensatedTimesKeepRawTaps()
    {
        var result = _service.ApplyContinuousUnitMarkers(Lyrics(), [At(0.9), At(1.9), At(3.9), At(4.9), At(6.9)], At(10), 0, At(9),
            [At(1), At(2), At(4), At(5), At(7)]);
        Assert.Equal(6.9, result.Lines[1].Units[^1].Timing.End.TotalSeconds);
        Assert.Equal(5, result.Lines[1].Units[^1].RawInputTime!.Value.TotalSeconds);
    }

    [Fact]
    public void NonIncreasingTimesInvalidEndAndMismatchedRawInputsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => _service.ApplyContinuousUnitMarkers(Lyrics(), [At(2), At(2)], At(10), 0, At(5)));
        Assert.Throws<ArgumentException>(() => _service.ApplyContinuousUnitMarkers(Lyrics(), [At(2), At(3)], At(10), 0, At(3)));
        Assert.Throws<ArgumentException>(() => _service.ApplyContinuousUnitMarkers(Lyrics(), [At(2), At(3)], At(10), 0, At(11)));
        Assert.Throws<ArgumentException>(() => _service.ApplyContinuousUnitMarkers(Lyrics(), [At(2), At(3)], At(10), 0, At(5), [At(2)]));
    }

    [Fact]
    public void InstrumentalAndDisplayOnlyLinesDoNotConsumeSpaceTaps()
    {
        var document = new LyricImportService().Parse("唱\n间奏\n显示\n完", ".txt", LyricLanguage.Chinese);
        document = document with { Lines = document.Lines.Select((line, index) => index switch
        { 1 => line with { IsInstrumental = true }, 2 => line with { IsHighlightEnabled = false }, _ => line }).ToArray() };
        Assert.Equal(new[] { "唱", "完" }, UnitTapPlan.Build(document, 0).Select(item => item.DisplayText));
        var result = _service.ApplyContinuousUnitMarkers(document, [At(1), At(5)], At(10), 0, At(6));
        Assert.Equal(document.Lines[1], result.Lines[1]); Assert.Equal(document.Lines[2], result.Lines[2]);
    }

    [Fact]
    public void JapaneseMoraSharingKanjiRemainDistinctSingingUnits()
    {
        var document = new LyricImportService().Parse("未来", ".txt", LyricLanguage.Japanese);
        var source = document.Lines[0].Units[0];
        document = document with { Lines = [document.Lines[0] with
        {
            Units = new[] { "み", "ら", "い" }.Select(reading => source with
            { Id = Guid.NewGuid(), Reading = reading, DisplayRanges = [new TextRange(0, 2)] }).ToArray(),
        }] };
        var plan = UnitTapPlan.Build(document, 0);
        Assert.Equal(new[] { "み", "ら", "い" }, plan.Select(target => target.Reading));
        var result = _service.ApplyContinuousUnitMarkers(document, [At(1), At(2), At(3)], At(10), 0, At(4));
        Assert.All(result.Lines[0].Units, unit => Assert.Equal(new TextRange(0, 2), Assert.Single(unit.DisplayRanges)));
        Assert.Equal(new[] { 1d, 2, 3 }, result.Lines[0].Units.Select(unit => unit.Timing.Start.TotalSeconds));
    }
}
