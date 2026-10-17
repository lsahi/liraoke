using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class LyricTextMappingTests
{
    [Theory]
    [InlineData("思", "9-9")]
    [InlineData("い", "10-10")]
    [InlineData("出", "11-11")]
    public void NarrowsSharedWordAndPrefersItsLocalOccurrence(string text, string expected)
    {
        var result = LyricTextMapping.Resolve("おかえりなさい 思い出に", text, [new TextRange(8, 3)]);
        Assert.Equal(expected, LyricTextMapping.Format(result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0-1")]
    [InlineData("4-4")]
    [InlineData("2-1")]
    [InlineData("1-2,2-3")]
    [InlineData("3-3,1-1")]
    [InlineData("x-y")]
    public void InvalidRangesAreRejected(string mapping) =>
        Assert.Throws<ArgumentException>(() => LyricTextMapping.Parse("思い出", mapping));

    [Theory]
    [InlineData("😀出", "1-1")]
    [InlineData("か\u3099出", "1-1")]
    public void CannotSplitGrapheme(string source, string mapping) =>
        Assert.Throws<ArgumentException>(() => LyricTextMapping.Parse(source, mapping));

    [Fact]
    public void AmbiguousAndMissingTextRequireExplicitCorrection()
    {
        Assert.Throws<ArgumentException>(() => LyricTextMapping.Resolve("い思い出", "い", [new TextRange(0, 4)]));
        Assert.Throws<ArgumentException>(() => LyricTextMapping.Resolve("思い出", "違", [new TextRange(0, 3)]));
        Assert.Throws<ArgumentException>(() => LyricTextMapping.Resolve("思い出", " ", [new TextRange(0, 3)]));
        Assert.Equal("3-3", LyricTextMapping.Format(LyricTextMapping.Parse("い思い出", "3-3")));
        Assert.Equal("3-3", LyricTextMapping.Format(LyricTextMapping.Resolve("思い出", "出", [new TextRange(0, 1)])));
    }
}
