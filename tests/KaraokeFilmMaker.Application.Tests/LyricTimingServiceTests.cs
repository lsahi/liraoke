using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class LyricTimingServiceTests
{
    [Fact]
    public void UnitMarkers_PreserveRawTapsAndEndAtLineBoundary()
    {
        LyricDocument document = new LyricImportService().Parse("[00:01]你好", ".lrc", LyricLanguage.Chinese);
        LyricLine result = new LyricTimingService().ApplyUnitMarkers(
            document.Lines[0], [ProjectTime.FromSeconds(2), ProjectTime.FromSeconds(4)]);
        Assert.Equal(ProjectTime.FromSeconds(2), result.Units[0].RawInputTime);
        Assert.Equal(ProjectTime.FromSeconds(4), result.Units[0].Timing.End);
        Assert.Equal(document.Lines[0].Timing.End, result.Units[1].Timing.End);
        Assert.All(result.Units, unit => Assert.True(unit.IsConfirmed));
    }

    [Fact]
    public void LineMarkers_CreateContiguousTimingAndUnitsForUntimedText()
    {
        LyricDocument document = new LyricImportService().Parse("你好\n世界", ".txt", LyricLanguage.Chinese);
        LyricDocument result = new LyricTimingService().ApplyLineMarkers(
            document, [ProjectTime.FromSeconds(1), ProjectTime.FromSeconds(3)], ProjectTime.FromSeconds(5));
        Assert.Equal(ProjectTime.FromSeconds(3), result.Lines[0].Timing.End);
        Assert.Equal(ProjectTime.FromSeconds(2), result.Lines[0].Units[0].Timing.End);
        Assert.Equal(ProjectTime.FromSeconds(5), result.Lines[1].Timing.End);
    }

    [Fact]
    public void LocalRecording_PreservesOtherLinesAndStopsAtNextTimedLine()
    {
        LyricDocument document = new LyricImportService().Parse(
            "[00:01]一\n[00:03]二\n[00:05]三", ".lrc", LyricLanguage.Chinese);
        LyricDocument result = new LyricTimingService().ApplyLineMarkers(
            document, [ProjectTime.FromSeconds(4)], ProjectTime.FromSeconds(10), firstLineIndex: 1);
        Assert.Equal(document.Lines[0], result.Lines[0]);
        Assert.Equal(document.Lines[2], result.Lines[2]);
        Assert.Equal(ProjectTime.FromSeconds(5), result.Lines[1].Timing.End);
    }

    [Fact]
    public void RecordingAfterBackwardSeek_RequiresCorrectionBeforeApplying()
    {
        LyricDocument document = new LyricImportService().Parse("一\n二", ".txt", LyricLanguage.Chinese);
        Assert.Throws<ArgumentException>(() => new LyricTimingService().ApplyLineMarkers(
            document, [ProjectTime.FromSeconds(3), ProjectTime.FromSeconds(2)], ProjectTime.FromSeconds(5)));
    }
}
