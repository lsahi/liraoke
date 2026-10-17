using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class AudioVisualizationMotionTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(25, 90)]
    [InlineData(50, 180)]
    [InlineData(100, 0)]
    [InlineData(125, 90)]
    public void RingMakesExactlyOneTurnPerHundredSongSeconds(double seconds, double expected) =>
        Assert.Equal(expected, AudioVisualizationMotion.RotationDegrees(ProjectTime.FromSeconds(seconds)));

    [Fact]
    public void DenseColumnsInterpolateOriginalFrequencyAxis()
    {
        float[] bands = [0, 1, 0];
        Assert.Equal(0, AudioVisualizationMotion.DenseBand(bands, 0, 5));
        Assert.Equal(0.5f, AudioVisualizationMotion.DenseBand(bands, 1, 5));
        Assert.Equal(1, AudioVisualizationMotion.DenseBand(bands, 2, 5));
        Assert.Equal(0, AudioVisualizationMotion.DenseBand(bands, 4, 5));
    }

    [Fact]
    public void FireworksAreLowMidDrivenBoundedAndIndependentOfSeekOrder()
    {
        var low = Data(band => band < 20 ? (byte)230 : (byte)0);
        var high = Data(band => band >= 20 ? (byte)255 : (byte)0);
        var silence = Data(_ => 0);
        var first = Sample(low, 0.3);
        Assert.NotEmpty(first);
        Assert.Empty(Sample(high, 0.3)); Assert.Empty(Sample(silence, 0.3));
        Assert.All(first, burst => { Assert.InRange(burst.X, 0.12, 0.88); Assert.InRange(burst.Y, 0.12, 0.8); });
        _ = Sample(low, 8.9);
        Assert.Equal(first, Sample(low, 0.3));
        Assert.Empty(Sample(low, 10));
        var positions = new HashSet<(double, double)>();
        for (int step = 0; step < 100; step++)
        {
            var bursts = Sample(low, step / 10.0);
            Assert.InRange(bursts.Length, 0, AudioVisualizationMotion.MaximumActiveBursts);
            foreach (var burst in bursts) { positions.Add((burst.X, burst.Y)); }
        }
        Assert.True(positions.Count > 4);
    }

    [Fact]
    public void SilentTailLetsExistingBurstsFadeThenStopsCompletely()
    {
        var frames = new byte[32 * 30 * 3]; Array.Fill(frames, (byte)255, 0, 32 * 15);
        var data = new AudioVisualizationData(3, frames);
        Assert.NotEmpty(Sample(data, 0.3));
        Assert.NotEmpty(Sample(data, 0.7));
        Assert.Empty(Sample(data, 2.2));
    }

    private static AudioVisualizationData Data(Func<int, byte> band) =>
        new(10, Enumerable.Range(0, 32 * 30 * 10).Select(index => band(index % 32)).ToArray());
    private static FireworkBurst[] Sample(AudioVisualizationData data, double time)
    {
        Span<FireworkBurst> bursts = stackalloc FireworkBurst[AudioVisualizationMotion.MaximumActiveBursts];
        int count = AudioVisualizationMotion.SampleFireworks(data, ProjectTime.FromSeconds(time), bursts);
        return bursts[..count].ToArray();
    }
}
