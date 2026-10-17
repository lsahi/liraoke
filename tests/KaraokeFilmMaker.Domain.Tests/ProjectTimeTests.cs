using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Domain.Tests;

public sealed class ProjectTimeTests
{
    [Fact]
    public void StoresTimeAsIntegerMicroseconds()
    {
        ProjectTime time = ProjectTime.FromSeconds(1.234567);
        Assert.Equal(1_234_567, time.Microseconds);
    }

    [Fact]
    public void RejectsNegativeTime()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectTime(-1));
    }

    [Fact]
    public void TimeRangeRejectsReversedEndpoints()
    {
        Assert.Throws<ArgumentException>(() =>
            new TimeRange(ProjectTime.FromSeconds(2), ProjectTime.FromSeconds(1)));
    }
}
