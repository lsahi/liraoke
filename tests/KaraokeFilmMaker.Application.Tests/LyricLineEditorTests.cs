using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class LyricLineEditorTests
{
    [Fact]
    public void MergePreservesSecondLineTextMappingAndTimings()
    {
        var document = new LyricImportService().Parse("[00:01]未来\n[00:03]進む", ".lrc", LyricLanguage.Japanese);
        LyricLine result = LyricLineEditor.Merge(document.Lines[0], document.Lines[1]);
        Assert.Equal("未来 進む", result.PrimaryText);
        Assert.Equal(3, result.Units[2].DisplayRanges[0].Start);
        Assert.Equal(document.Lines[1].Units[0].Timing, result.Units[2].Timing);
    }

    [Fact]
    public void SplitCreatesNewLineAndKeepsTimeRangeContiguous()
    {
        var document = new LyricImportService().Parse("[00:01]未来へ進む", ".lrc", LyricLanguage.Japanese);
        var split = LyricLineEditor.Split(document.Lines[0], 3, LyricLanguage.Japanese);
        Assert.Equal("未来へ", split.Left.PrimaryText); Assert.Equal("進む", split.Right.PrimaryText);
        Assert.Equal(split.Left.Timing.End, split.Right.Timing.Start);
        Assert.NotEqual(split.Left.Id, split.Right.Id);
        Assert.All(split.Right.Units, unit => Assert.All(unit.DisplayRanges, range => Assert.True(range.EndExclusive <= 2)));
    }

    [Fact]
    public void OffsetChangesEditedTimingButPreservesRawTap()
    {
        var document = new LyricImportService().Parse("[00:01]你", ".lrc", LyricLanguage.Chinese);
        var line = document.Lines[0] with
        {
            Units = [document.Lines[0].Units[0] with { RawInputTime = ProjectTime.FromSeconds(1) }],
        };
        var moved = LyricLineEditor.Offset(line, 200_000);
        Assert.Equal(ProjectTime.FromSeconds(1.2), moved.Units[0].Timing.Start);
        Assert.Equal(ProjectTime.FromSeconds(1), moved.Units[0].RawInputTime);
        Assert.Throws<ArgumentOutOfRangeException>(() => LyricLineEditor.Offset(line, -2_000_000));
    }
}
