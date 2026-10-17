using KaraokeFilmMaker.Application.Timing;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class MetronomeGridTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 250000)]
    [InlineData(3, 166667)]
    [InlineData(4, 125000)]
    [InlineData(6, 166667)]
    [InlineData(8, 125000)]
    public void SupportedDivisionsSnapWithinOneBeat(int divisions, long expectedMicroseconds)
    {
        var settings = new MetronomeSettings(true) { SnapDivisions = divisions };
        Assert.Equal(new ProjectTime(expectedMicroseconds), MetronomeGrid.Snap(ProjectTime.FromMilliseconds(140), settings));
        Assert.Equal(ProjectTime.FromMilliseconds(140), MetronomeGrid.Snap(ProjectTime.FromMilliseconds(140), settings with { Enabled = false }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(16)]
    public void InvalidDivisionIsRejected(int divisions) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        MetronomeGrid.Snap(ProjectTime.Zero, new MetronomeSettings(true) { SnapDivisions = divisions }));

    [Fact]
    public void SubdivisionPreservesOffsetFutureTiesAndSongBounds()
    {
        var settings = new MetronomeSettings(true, 120, 100) { SnapDivisions = 4 };
        Assert.Equal(ProjectTime.FromMilliseconds(225), MetronomeGrid.Snap(new ProjectTime(162500), settings));
        Assert.False(MetronomeGrid.TrySnap(ProjectTime.Zero, settings, ProjectDuration.FromMilliseconds(99), out _));
        Assert.Equal(ProjectTime.FromMilliseconds(225), MetronomeGrid.Snap(ProjectTime.FromMilliseconds(300), settings, ProjectDuration.FromMilliseconds(300)));
        Assert.Equal(ProjectTime.FromMilliseconds(25), MetronomeGrid.Snap(ProjectTime.Zero, settings with { OffsetMilliseconds = -100 }));
    }

    [Fact]
    public void TripletGridDoesNotAccumulateRoundingDrift()
    {
        var settings = new MetronomeSettings(true, 123.45, -137) { SnapDivisions = 3 };
        decimal expected = -137000m + 100000 * (60_000_000m / 123.45m / 3);
        long exact = (long)decimal.Round(expected, 0, MidpointRounding.AwayFromZero);
        Assert.Equal(new ProjectTime(exact), MetronomeGrid.Snap(new ProjectTime(exact + 1000), settings));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(249, 0, 0)]
    [InlineData(250, 0, 500)]
    [InlineData(749, 0, 500)]
    [InlineData(750, 0, 1000)]
    [InlineData(20, 100, 100)]
    [InlineData(350, 100, 600)]
    [InlineData(20, -100, 400)]
    [InlineData(650, -100, 900)]
    [InlineData(250, 600000, 600000)]
    public void SnapsToNearestBeat_WithFutureMidpointTie(int inputMs, int offsetMs, int expectedMs)
    {
        var result = MetronomeGrid.Snap(ProjectTime.FromMilliseconds(inputMs),
            new MetronomeSettings(true, 120, offsetMs));
        Assert.Equal(ProjectTime.FromMilliseconds(expectedMs), result);
    }

    [Fact]
    public void SongEndSelectsLastValidBeat_NotAnOffGridClampedTime()
    {
        var result = MetronomeGrid.Snap(ProjectTime.FromMilliseconds(1400),
            new MetronomeSettings(true), ProjectDuration.FromMilliseconds(1400));
        Assert.Equal(ProjectTime.FromMilliseconds(1000), result);
    }

    [Fact]
    public void NoBeatInShortSong_IsExplicitlyRejected()
    {
        var settings = new MetronomeSettings(true, 120, 100);
        Assert.False(MetronomeGrid.TrySnap(ProjectTime.Zero, settings, ProjectDuration.FromMilliseconds(50), out _));
        Assert.Throws<InvalidOperationException>(() => MetronomeGrid.Snap(ProjectTime.Zero, settings,
            ProjectDuration.FromMilliseconds(50)));
    }

    [Fact]
    public void PositiveOffsetAfterSong_DoesNotWrapBackwards()
    {
        Assert.False(MetronomeGrid.TrySnap(ProjectTime.FromSeconds(1), new MetronomeSettings(true, 120, 5000),
            ProjectDuration.FromSeconds(2), out _));
    }

    [Fact]
    public void DisabledDoesNotQuantize()
    {
        var input = ProjectTime.FromMilliseconds(123);
        Assert.Equal(input, MetronomeGrid.Snap(input, new MetronomeSettings()));
        Assert.Equal(ProjectTime.FromMilliseconds(100),
            MetronomeGrid.Snap(input, new MetronomeSettings(), ProjectDuration.FromMilliseconds(100)));
    }

    [Fact]
    public void FractionalBpmUsesAbsoluteGridWithoutAccumulatedDrift()
    {
        var settings = new MetronomeSettings(true, 123.45, -137);
        const long beat = 100000;
        double beatSeconds = settings.OffsetMilliseconds / 1000d + beat * 60 / settings.BeatsPerMinute;
        var result = MetronomeGrid.Snap(ProjectTime.FromSeconds(beatSeconds + 0.01), settings);
        Assert.InRange(Math.Abs(result.TotalSeconds - beatSeconds), 0, 0.000001);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(19.9, 0)]
    [InlineData(400.1, 0)]
    [InlineData(120, -600001)]
    [InlineData(120, 600001)]
    public void InvalidConfigurationIsRejected(double bpm, int offset)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MetronomeGrid.Snap(ProjectTime.Zero,
            new MetronomeSettings(true, bpm, offset)));
    }
}
