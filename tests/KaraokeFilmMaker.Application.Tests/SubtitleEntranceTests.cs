using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class SubtitleEntranceTests
{
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(13.999999, false, 0)]
    [InlineData(14, true, 0)]
    [InlineData(16, true, 1.0 / 3)]
    [InlineData(18, true, 2.0 / 3)]
    [InlineData(20, true, 1)]
    [InlineData(22, false, 0)]
    public void IntroShowsOnlySixSecondsBeforeStart(double seconds, bool visible, double progress)
    {
        var line = Line(20, 22);
        var scene = Build([line], seconds);
        if (!visible) { Assert.Empty(scene.Lines); return; }
        var item = Assert.Single(scene.Lines);
        Assert.Equal(progress, item.EntranceProgress!.Value, 8);
        Assert.Equal(seconds >= 20, item.IsActive);
        Assert.Equal(line, item.Line);
    }

    [Fact]
    public void EachUpcomingLineHasItsOwnLeadWindowAndSlotsRemainStable()
    {
        var first = Line(0, 3); var next = Line(20, 22); var third = Line(23, 25);
        LyricLine[] lines = [first, next, third];
        Assert.Single(Build(lines, 2).Lines);
        Assert.Empty(Build(lines, 4).Lines);
        var lead = Assert.Single(Build(lines, 14).Lines);
        Assert.Equal(next.Id, lead.Line.Id); Assert.Equal(SubtitleSlot.BottomRight, lead.Slot);
        var both = Build(lines, 17).Lines;
        Assert.Equal(2, both.Count); Assert.Null(both[1].EntranceProgress);
        Assert.Equal(SubtitleSlot.TopLeft, both[1].Slot);
        Assert.Equal(SubtitleSlot.BottomRight, Build(lines, 20).Lines[0].Slot);
        Assert.Null(Assert.Single(Build(lines, 23).Lines).EntranceProgress);
    }

    [Theory]
    [InlineData(5.999999, false)]
    [InlineData(6, true)]
    [InlineData(6.000001, true)]
    public void LongGapThresholdIsInclusive(double gap, bool cue)
    {
        var next = Line(2 + gap, 4 + gap);
        var item = Assert.Single(Build([Line(0, 2), next], next.Timing.Start.TotalSeconds - 1).Lines);
        Assert.Equal(cue, item.EntranceProgress.HasValue);
    }

    [Fact]
    public void OverlapsDoNotCountAsSilenceAndInstrumentalRowsDoNotConsumeSlots()
    {
        var overlap = Build([Line(0, 18), Line(1, 2), Line(20, 22)], 19);
        Assert.Null(Assert.Single(overlap.Lines).EntranceProgress);
        var instrumental = Line(2, 19) with { IsInstrumental = true };
        var item = Assert.Single(Build([Line(0, 2), instrumental, Line(3, 3), Line(20, 22)], 14).Lines);
        Assert.NotNull(item.EntranceProgress); Assert.Equal(SubtitleSlot.BottomRight, item.Slot);
    }

    [Fact]
    public void EditsAndSeekingRecomputeCueWithoutChangingLyrics()
    {
        var first = Line(0, 2); var second = Line(20, 22);
        var builder = new SubtitleSceneBuilder(); var document = new LyricDocument(LyricLanguage.Japanese, [first, second]);
        var initial = builder.Build(document, ProjectTime.FromSeconds(18)).Lines.Single();
        _ = builder.Build(document, ProjectTime.FromSeconds(21));
        var repeated = builder.Build(document, ProjectTime.FromSeconds(18)).Lines.Single();
        Assert.Equal(initial.EntranceProgress, repeated.EntranceProgress);
        Assert.Equal(initial.Slot, repeated.Slot); Assert.Same(second, repeated.Line);
        var edited = document with { Lines = [first with { Timing = new(ProjectTime.Zero, ProjectTime.FromSeconds(16)) }, second] };
        Assert.Null(builder.Build(edited, ProjectTime.FromSeconds(18)).Lines.Single().EntranceProgress);
    }

    private static SubtitleScene Build(LyricLine[] lines, double seconds) =>
        new SubtitleSceneBuilder().Build(new(LyricLanguage.Chinese, lines), ProjectTime.FromSeconds(seconds));
    private static LyricLine Line(double start, double end) => new(Guid.NewGuid(), "歌", AnnotationType.None, null, null,
        new(ProjectTime.FromSeconds(start), ProjectTime.FromSeconds(end)), [], ReviewState.Confirmed, null);
}
