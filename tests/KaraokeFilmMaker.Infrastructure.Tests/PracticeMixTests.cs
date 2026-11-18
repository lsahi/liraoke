using System.Runtime.InteropServices;
using KaraokeFilmMaker.Infrastructure.Audio;
using NAudio.Wave;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public class PracticeMixTests
{
    [Fact]
    public void SongAndMicrophoneHaveSeparateGainsAndEndDoesNotStopMonitor()
    {
        var mic = new ConstantSource(.8f);
        var mix = new PracticeMixProvider(new ConstantSource(.6f), mic, 4)
        { SongVolume = .5f, MicrophoneVolume = .25f, MicrophoneMuted = false };
        var samples = new float[8];
        Assert.Equal(8, mix.Read(samples));
        Assert.All(samples[..4], sample => Assert.Equal(.5f, sample, 5));
        Assert.All(samples[4..], sample => Assert.Equal(.2f, sample, 5));
        mix.MicrophoneMuted = true;
        mix.Read(samples);
        Assert.All(samples, sample => Assert.Equal(0, sample));
        Assert.Equal(16, mic.ReadSamples); // Muting drains the mic.
        mix.MicrophoneMuted = false;
        mix.Read(samples);
        Assert.All(samples, sample => Assert.Equal(.2f, sample, 5));
    }

    [Fact]
    public void MixClampsPeaksAndNeverLeaksPastCropBoundary()
    {
        var mix = new PracticeMixProvider(new ConstantSource(1), new ConstantSource(1), 2)
        { SongVolume = 1, MicrophoneVolume = 1, MicrophoneMuted = false };
        float[] samples = new float[6]; mix.Read(samples);
        Assert.All(samples, sample => Assert.Equal(1, sample));
        mix.MicrophoneMuted = true; mix.Read(samples);
        Assert.All(samples, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void LiveBufferDropsStaleVoiceAndUnderflowIsSilence()
    {
        var buffer = new PracticeMonitorBuffer(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        buffer.Add(MemoryMarshal.AsBytes(Enumerable.Repeat(.1f, 4800).ToArray().AsSpan()));
        buffer.Add(MemoryMarshal.AsBytes(Enumerable.Repeat(.7f, 4800).ToArray().AsSpan()));
        Assert.Equal(4800 * 4, buffer.BufferedBytes);
        float[] samples = new float[5000]; buffer.Read(MemoryMarshal.AsBytes(samples.AsSpan()));
        Assert.All(samples[..4800], sample => Assert.Equal(.7f, sample));
        Assert.All(samples[4800..], sample => Assert.Equal(0, sample));
        buffer.Add(new byte[48000]);
        Assert.InRange(buffer.BufferedBytes, 0, 48000 * 8 * 60 / 1000);
        buffer.Clear(); Assert.Equal(0, buffer.BufferedBytes);
    }
    [Fact]
    public void Vt3ResamplingDoesNotIntroduceLargeLookahead()
    {
        var buffer = new PracticeMonitorBuffer(WaveFormat.CreateIeeeFloatWaveFormat(96000, 2));
        float[] input = new float[1920]; input[80] = input[81] = 1;
        buffer.Add(MemoryMarshal.AsBytes(input.AsSpan()));
        var resampler = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(buffer.ToSampleProvider(), 48000);
        float[] output = new float[960]; resampler.Read(output);
        int peak = Enumerable.Range(0, output.Length / 2).MaxBy(frame => Math.Abs(output[frame * 2]));
        Assert.InRange(peak, 18, 25);
        Assert.Equal(0, buffer.BufferedBytes);
    }
    private sealed class ConstantSource(float value) : ISampleProvider
    {
        public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int ReadSamples { get; private set; }
        public int Read(Span<float> buffer) { buffer.Fill(value); ReadSamples += buffer.Length; return buffer.Length; }
    }
}

// Matches the existing CI filter on hosts without WASAPI endpoints.
public class PracticeWasapiPlaybackTests
{
    [Fact]
    public async Task RenderedClockPauseSeekCropReplayReconnectAndDispose()
    {
        string root = Path.Combine(Path.GetTempPath(), "Liraoke.PracticeAudio", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "silence.wav");
        try
        {
            using (var writer = new WaveFileWriter(file, new WaveFormat(48000, 16, 2))) writer.Write(new byte[48000 * 4 * 3]);
            await using var session = new PracticeAudioSession();
            var range = new KaraokeFilmMaker.Domain.Time.TimeRange(At(.2), At(1));
            await session.OpenAsync(file, range, null);
            Assert.False(session.IsPlaying); Assert.False(session.IsMonitoring);
            await session.PlayAsync(); await Task.Delay(160); await session.PauseAsync();
            double frozen = session.Position.TotalSeconds;
            Assert.InRange(frozen, .21, .8);
            await Task.Delay(100); Assert.Equal(frozen, session.Position.TotalSeconds, 4);
            await session.SeekAsync(At(0)); Assert.Equal(.2, session.Position.TotalSeconds, 4);
            await session.SeekAsync(At(.85)); await session.PlayAsync(); await Task.Delay(300);
            Assert.False(session.IsPlaying); Assert.Equal(1, session.Position.TotalSeconds, 3);
            await session.PlayAsync(); await Task.Delay(70); await session.PauseAsync();
            Assert.InRange(session.Position.TotalSeconds, .2, .7);
            await session.SeekAsync(At(2)); Assert.Equal(1, session.Position.TotalSeconds, 4);
            var devices = PracticeAudioSession.GetDevices(false); Assert.NotEmpty(devices);
            await session.OpenAsync(file, range, devices[0].Id);
            var inputs = PracticeAudioSession.GetDevices(true);
            if (inputs.Count > 0)
            {
                // Validate a real input endpoint without writing or audibly monitoring captured samples.
                session.MicrophoneMuted = true; session.SongVolume = 0;
                await session.SetMonitoringAsync(inputs[0].Id, true);
                await Task.Delay(150);
                Assert.True(session.IsMonitoring); Assert.Null(session.DeviceError);
                await session.PauseAsync();
                await session.SetMonitoringAsync(null, false);
                Assert.False(session.IsMonitoring);
            }
            await Assert.ThrowsAnyAsync<Exception>(() => session.SetMonitoringAsync("invalid-test-device", true));
            Assert.False(session.IsMonitoring);
            await session.PlayAsync(); await Task.Delay(60); await session.PauseAsync();
            await session.DisconnectAsync();
            await session.OpenAsync(file, range, null);
            await session.DisposeAsync(); await session.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.PlayAsync());
            File.Delete(file); Assert.False(File.Exists(file));
        }
        finally { Directory.Delete(root, true); }
    }
    private static KaraokeFilmMaker.Domain.Time.ProjectTime At(double value) => KaraokeFilmMaker.Domain.Time.ProjectTime.FromSeconds(value);
}
