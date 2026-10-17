using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class LyricImportServiceTests
{
    private readonly LyricImportService _service = new();

    [Fact]
    public void ParsesPlainJapaneseAndCombinesSmallKana()
    {
        LyricDocument document = _service.Parse("きょう、未来へ", ".txt", LyricLanguage.Japanese);

        var line = Assert.Single(document.Lines);
        Assert.Equal("きょう、未来へ", line.PrimaryText);
        Assert.Equal("きょ", line.Units[0].Reading);
        Assert.DoesNotContain(line.Units, unit => unit.Reading == "、");
    }

    [Fact]
    public void ParsesLrcFractionsAndUsesNextLineAsEnd()
    {
        const string lrc = "[00:01.25]未来へ\n[00:03.500]進もう";

        LyricDocument document = _service.Parse(lrc, ".lrc", LyricLanguage.Japanese);

        Assert.Equal(1_250_000, document.Lines[0].Timing.Start.Microseconds);
        Assert.Equal(3_500_000, document.Lines[0].Timing.End.Microseconds);
        Assert.Equal(8_500_000, document.Lines[1].Timing.End.Microseconds);
    }

    [Fact]
    public void ParsesSrtTimingAndText()
    {
        const string srt = "1\n00:00:02,100 --> 00:00:04,250\n明日へ進む\n";

        LyricDocument document = _service.Parse(srt, ".srt", LyricLanguage.Japanese);

        var line = Assert.Single(document.Lines);
        Assert.Equal("明日へ進む", line.PrimaryText);
        Assert.Equal(2_100_000, line.Timing.Start.Microseconds);
        Assert.Equal(4_250_000, line.Timing.End.Microseconds);
        Assert.Equal(line.Timing.Start, line.Units[0].Timing.Start);
        Assert.Equal(line.Timing.End, line.Units[^1].Timing.End);
    }

    [Fact]
    public void RejectsUnknownFormat()
    {
        Assert.Throws<NotSupportedException>(() =>
            _service.Parse("lyrics", ".ass", LyricLanguage.Chinese));
    }
}
