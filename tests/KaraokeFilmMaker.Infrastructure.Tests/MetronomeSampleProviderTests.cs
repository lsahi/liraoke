using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Audio;
using NAudio.Wave;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class MetronomeSampleProviderTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void SnapDivisionsDoNotChangeAudibleWholeBeatClicks(int divisions)
    {
        var settings = new MetronomeSettings(true, 123.45, -137);
        Assert.Equal(Render(2, settings), Render(2, settings with { SnapDivisions = divisions }));
    }

    private const int SampleRate = 48000;

    [Fact]
    public void ClicksShareMusicSamplesAndStopAtSourceEnd()
    {
        var samples = Render(1.1, new MetronomeSettings(true));
        Assert.True(samples[0] > 0.4);
        Assert.True(samples[SampleRate / 2] > 0.4);
        Assert.True(samples[SampleRate] > 0.4);
        Assert.Equal(0, samples[SampleRate / 4]);
        Assert.Equal((int)(SampleRate * 1.1), samples.Length);
    }

    [Theory]
    [InlineData(100, 0.1)]
    [InlineData(-100, 0.4)]
    public void SignedOffsetPositionsClicks(int offset, double firstBeat)
    {
        var samples = Render(1, new MetronomeSettings(true, 120, offset));
        Assert.Equal(0, samples[0]);
        Assert.True(samples[(int)(firstBeat * SampleRate)] > 0.4);
        Assert.True(samples[(int)((firstBeat + 0.5) * SampleRate)] > 0.4);
    }

    [Fact]
    public void PositiveOffsetDoesNotCreateBeatsBeforeAnchor()
    {
        Assert.All(Render(2, new MetronomeSettings(true, 120, 5000)), value => Assert.Equal(0, value));
        var samples = Render(2, new MetronomeSettings(true, 120, 1500));
        Assert.All(samples.Take(SampleRate * 3 / 2), value => Assert.Equal(0, value));
        Assert.True(samples[SampleRate * 3 / 2] > 0.4);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1)]
    [InlineData(1.5)]
    public void PlaybackSpeedMapsPhysicalSamplesToOriginalSongGrid(double rate)
    {
        var samples = Render(1.1 / rate, new MetronomeSettings(true), projectRate: rate);
        int beatFrame = (int)Math.Ceiling(SampleRate * 0.5 / rate);
        Assert.True(samples[beatFrame] > 0.39);
        Assert.Equal(0, samples[beatFrame + SampleRate / 10]);
    }

    [Fact]
    public void SeekingAndResumingReconstructsIdenticalClickPhase()
    {
        var settings = new MetronomeSettings(true, 123.45, -137);
        float[] continuous = Render(4, settings);
        const double seek = 2.125;
        float[] resumed = Render(1, settings, physicalStartSeconds: seek);
        int frame = (int)(seek * SampleRate);
        for (int i = 0; i < resumed.Length; i++)
        {
            Assert.InRange(Math.Abs(continuous[frame + i] - resumed[i]), 0, 0.000001f);
        }
    }

    [Fact]
    public void BufferSizesAndStereoChannelsDoNotAlterBeatGrid()
    {
        var settings = new MetronomeSettings(true, 127, -17);
        float[] mono = Render(2, settings, blockSize: 8192);
        float[] stereo = Render(2, settings, channels: 2, blockSize: 101);
        Assert.Equal(mono.Length * 2, stereo.Length);
        for (int i = 0; i < mono.Length; i++)
        {
            Assert.Equal(mono[i], stereo[i * 2]);
            Assert.Equal(mono[i], stereo[i * 2 + 1]);
        }
    }

    [Fact]
    public void DisabledLeavesAudioUntouched_EnabledMixIsBounded()
    {
        Assert.All(Render(0.1, new MetronomeSettings(), sourceValue: 0.8f), value => Assert.Equal(0.8f, value));
        var mixed = Render(0.1, new MetronomeSettings(true), sourceValue: 0.8f);
        Assert.Equal(1f, mixed[0]);
        Assert.All(mixed, value => Assert.InRange(value, -1f, 1f));
        Assert.Equal(0.8f, mixed[SampleRate / 20]);
    }

    [Fact]
    public void LongSeekHasNoAccumulatedTimingDrift()
    {
        var settings = new MetronomeSettings(true, 123.45, -137);
        double beatSeconds = -0.137 + 100000 * 60 / 123.45;
        var samples = Render(0.1, settings, physicalStartSeconds: beatSeconds);
        Assert.True(samples[0] > 0.4);
        Assert.Equal(0, samples[SampleRate / 20]);
    }

    [Fact]
    public async Task ConfigurationCanBeSetBeforeLoadingWithoutAudioDeviceAccess()
    {
        await using var session = new ScaledAudioPlaybackSession();
        await session.ConfigureMetronomeAsync(new MetronomeSettings(true, 132, -50));
        Assert.False(session.IsLoaded);
        Assert.False(session.IsPlaying);
        Assert.Equal(ProjectTime.Zero, session.Position);
    }

    private static float[] Render(double durationSeconds, MetronomeSettings settings, int channels = 1,
        int blockSize = 4096, double physicalStartSeconds = 0, double projectRate = 1, float sourceValue = 0)
    {
        int count = (int)Math.Round(durationSeconds * SampleRate) * channels;
        var provider = new MetronomeSampleProvider(new ConstantSource(count, channels, sourceValue), settings,
            physicalStartSeconds, projectRate);
        List<float> result = [];
        float[] buffer = new float[blockSize];
        int read;
        while ((read = provider.Read(buffer)) > 0) { result.AddRange(buffer.Take(read)); }
        Assert.Equal(0, provider.Read(buffer));
        return result.ToArray();
    }

    private sealed class ConstantSource(int remaining, int channels, float value) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, channels);

        public int Read(Span<float> buffer)
        {
            int count = Math.Min(buffer.Length, remaining);
            buffer[..count].Fill(value);
            remaining -= count;
            return count;
        }
    }
}
