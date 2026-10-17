using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class AudioVisualizationDataTests
{
    [Fact]
    public void InterpolationIsStableWhenSeekingBackAndNeverMutatesCallerData()
    {
        var bytes = new byte[64];
        Array.Fill(bytes, (byte)255, 32, 32);
        var data = new AudioVisualizationData(2d / 30, bytes);
        Array.Fill(bytes, (byte)0);
        var first = new float[32];
        var returned = new float[32];
        data.Sample(ProjectTime.FromSeconds(1d / 60), first);
        Assert.All(first, value => Assert.InRange(value, 0.4999f, 0.5001f));
        data.Sample(ProjectTime.FromSeconds(100), returned);
        Assert.All(returned, value => Assert.Equal(0, value));
        data.Sample(ProjectTime.FromSeconds(1d / 60), returned);
        Assert.Equal(first, returned);
    }

    [Fact]
    public void EmptySongAndEndOfSongAreSilent()
    {
        var values = Enumerable.Repeat(1f, 32).ToArray();
        new AudioVisualizationData(0, []).Sample(ProjectTime.Zero, values);
        Assert.All(values, value => Assert.Equal(0, value));
        var data = new AudioVisualizationData(1d / 30, Enumerable.Repeat((byte)255, 32).ToArray());
        data.Sample(ProjectTime.FromSeconds(1), values);
        Assert.All(values, value => Assert.Equal(0, value));
        data.Sample(ProjectTime.Zero, values);
        Assert.All(values, value => Assert.Equal(1, value));
    }

    [Fact]
    public void SamplesDoNotAllocateAndDoNotWriteBeyondBandCount()
    {
        var data = new AudioVisualizationData(1d / 30, new byte[32]);
        var values = Enumerable.Repeat(-1f, 33).ToArray();
        data.Sample(ProjectTime.Zero, values);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 100; index++) { data.Sample(ProjectTime.Zero, values); }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.Equal(-1, values[32]);
        Assert.Throws<ArgumentException>(() => data.Sample(ProjectTime.Zero, new float[31]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(14401)]
    public void RejectsInvalidDuration(double duration) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioVisualizationData(duration, []));

    [Fact]
    public void RejectsMismatchedAndPartialFrames()
    {
        Assert.Throws<ArgumentException>(() => new AudioVisualizationData(1, new byte[31]));
        Assert.Throws<ArgumentException>(() => new AudioVisualizationData(1, new byte[32]));
    }
}
