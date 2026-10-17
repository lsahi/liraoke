using System.Diagnostics;
using KaraokeFilmMaker.Application.Timing;
using KaraokeFilmMaker.Infrastructure.Audio;
using KaraokeFilmMaker.Infrastructure.Media;
using NAudio.Wave;
using Xunit.Abstractions;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class TempoAnalysisTests(ITestOutputHelper output)
{
    private const int Rate = OfflineTempoAnalysisService.AnalysisSampleRate;

    [Theory]
    [InlineData(73, 125)]
    [InlineData(96, 310)]
    [InlineData(120, 125)]
    [InlineData(137.5, 80)]
    [InlineData(180, 190)]
    [InlineData(210, 35)]
    public void DampedPitchedDrumAttacksRecoverTempoAndOriginalPhase(double bpm, int offset)
    {
        TempoAnalysisResult result = ConstantTempoEstimator.Estimate(Drums(bpm, offset, 36), Rate);
        Assert.True(result.IsReliable, result.Message);
        Assert.InRange(result.BeatsPerMinute!.Value, bpm - 0.08, bpm + 0.08);
        Assert.InRange(result.OffsetMilliseconds!.Value, offset - 15, offset + 15);
        Assert.InRange(result.Confidence, 0.8, 1);
        Assert.Contains(result.AlternativeBeatsPerMinute, alternative => Math.Abs(alternative - bpm / 2) < 0.1);
    }

    [Fact]
    public void BackgroundNoiseAndAccentedEveryFourthBeatKeepOriginalTempo()
    {
        float[] audio = Drums(128, 160, 40, noise: 0.025, accentEveryFour: true);
        TempoAnalysisResult result = ConstantTempoEstimator.Estimate(audio, Rate);
        Assert.True(result.IsReliable);
        Assert.InRange(result.BeatsPerMinute!.Value, 127.9, 128.1);
        Assert.InRange(result.OffsetMilliseconds!.Value, 145, 175);
        Assert.Contains("不代表小节重拍", result.Message);
    }

    [Fact]
    public void LongIntroPreservesPhaseRelativeToWholeSongInsteadOfShiftingToAnalysisRegion()
    {
        float[] audio = Drums(100, 230, 48, introSeconds: 12);
        TempoAnalysisResult result = ConstantTempoEstimator.Estimate(audio, Rate);
        Assert.True(result.IsReliable);
        Assert.InRange(result.BeatsPerMinute!.Value, 99.9, 100.1);
        Assert.InRange(result.OffsetMilliseconds!.Value, 215, 245);
    }

    [Fact]
    public void SilenceAndConstantPitchedToneHaveNoReliableRecommendation()
    {
        Assert.False(ConstantTempoEstimator.Estimate(new float[Rate * 20], Rate).IsReliable);
        float[] tone = Enumerable.Range(0, Rate * 20).Select(index => (float)(0.3 * Math.Sin(2 * Math.PI * 440 * index / Rate))).ToArray();
        TempoAnalysisResult result = ConstantTempoEstimator.Estimate(tone, Rate);
        Assert.False(result.IsReliable);
    }

    [Fact]
    public void ShortAudioAndUnstructuredNoiseDoNotInitializeMetronome()
    {
        TempoAnalysisResult shortResult = ConstantTempoEstimator.Estimate(Drums(120, 100, 4), Rate);
        Assert.False(shortResult.IsReliable);
        Assert.Null(shortResult.BeatsPerMinute);
        var random = new Random(701);
        float[] noise = Enumerable.Range(0, Rate * 20).Select(_ => (float)(random.NextDouble() * 0.5 - 0.25)).ToArray();
        Assert.False(ConstantTempoEstimator.Estimate(noise, Rate).IsReliable);
    }

    [Fact]
    public void TempoChangeAcrossSongIsNotPresentedAsStable()
    {
        float[] first = Drums(96, 100, 20);
        float[] second = Drums(137, 175, 20);
        TempoAnalysisResult result = ConstantTempoEstimator.Estimate(first.Concat(second).ToArray(), Rate);
        Assert.False(result.IsReliable, $"Unexpected {result.BeatsPerMinute} / stability {result.Confidence}");
    }

    [Fact]
    public void ExactImpulseTimestampsDoNotGainAnAnalysisFrameDelay()
    {
        float[] samples = new float[Rate * 30];
        for (double seconds = 0.2; seconds < 30; seconds += 0.5) { samples[(int)Math.Round(seconds * Rate)] = 1; }
        TempoAnalysisResult result = ConstantTempoEstimator.Estimate(samples, Rate);
        Assert.True(result.IsReliable);
        Assert.InRange(result.OffsetMilliseconds!.Value, 193, 203);
        Assert.InRange(result.BeatsPerMinute!.Value, 119.95, 120.05);
    }

    [Fact]
    public void EstimatorStopsAtNinetySecondsAndSupportsCancellation()
    {
        float[] first = Drums(117, 200, 90);
        float[] rest = Drums(159, 5, 15);
        TempoAnalysisResult a = ConstantTempoEstimator.Estimate(first, Rate);
        TempoAnalysisResult b = ConstantTempoEstimator.Estimate(first.Concat(rest).ToArray(), Rate);
        Assert.Equal(a.BeatsPerMinute, b.BeatsPerMinute);
        Assert.Equal(a.OffsetMilliseconds, b.OffsetMilliseconds);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ConstantTempoEstimator.Estimate(first, Rate, cancellation.Token));
    }

    [Fact]
    public void NinetySecondCpuAnalysisReportsMeasuredPerformanceWithoutDeviceSpeedGuarantee()
    {
        float[] samples = Drums(137.5, 170, 90, noise: 0.01, accentEveryFour: true);
        _ = ConstantTempoEstimator.Estimate(samples.AsSpan(0, Rate * 8), Rate); // Exclude the first JIT invocation.
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        var elapsed = Stopwatch.StartNew();
        TempoAnalysisResult result = ConstantTempoEstimator.Estimate(samples, Rate);
        elapsed.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        output.WriteLine($"90 s synthetic audio: CPU analysis {elapsed.Elapsed.TotalMilliseconds:0.0} ms; "
            + $"thread allocations {allocated / 1024d / 1024:0.00} MiB; "
            + $"BPM={result.BeatsPerMinute:0.00}, offset={result.OffsetMilliseconds} ms, stability={result.Confidence:0.000}. "
            + "This machine only; excludes source decoding and is not a speed guarantee.");
        Assert.True(result.IsReliable);
        Assert.InRange(result.BeatsPerMinute!.Value, 137.4, 137.6);
        Assert.InRange(result.OffsetMilliseconds!.Value, 155, 185);
    }

    [Fact]
    public async Task ServiceAnalyzesNinetySecondsOfOriginalMediaThroughBoundedFfmpegDecoder()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Liraoke.TempoTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string wave = Path.Combine(directory, "original song.wav");
        try
        {
            float[] audio = Drums(132, 140, 90);
            using (var writer = new WaveFileWriter(wave, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1)))
            {
                writer.WriteSamples(audio, 0, audio.Length);
            }
            long beforeLength = new FileInfo(wave).Length;
            var service = new OfflineTempoAnalysisService(MediaToolchain.Find());
            var elapsed = Stopwatch.StartNew();
            TempoAnalysisResult result = await service.AnalyzeAsync(wave);
            elapsed.Stop();
            output.WriteLine($"90 s original WAV → FFmpeg mono decode + CPU analysis + temporary cleanup: "
                + $"{elapsed.Elapsed.TotalMilliseconds:0.0} ms on this machine; not a speed guarantee. "
                + $"BPM={result.BeatsPerMinute:0.00}, offset={result.OffsetMilliseconds} ms.");
            Assert.True(result.IsReliable);
            Assert.InRange(result.BeatsPerMinute!.Value, 131.9, 132.1);
            Assert.InRange(result.OffsetMilliseconds!.Value, 125, 155);
            Assert.Equal(beforeLength, new FileInfo(wave).Length);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AnalyzeAsync(wave, cancellation.Token));
        }
        finally
        {
            File.Delete(wave);
            Directory.Delete(directory);
        }
    }

    private static float[] Drums(double bpm, int offsetMilliseconds, int seconds, double noise = 0,
        bool accentEveryFour = false, double introSeconds = 0)
    {
        var audio = new float[Rate * seconds];
        var random = new Random(12345);
        for (int index = 0; index < audio.Length; index++) { audio[index] = (float)((random.NextDouble() * 2 - 1) * noise); }
        int beat = 0;
        for (double start = offsetMilliseconds / 1000d; start < seconds; start += 60 / bpm, beat++)
        {
            if (start < introSeconds) { continue; }
            int first = (int)Math.Round(start * Rate);
            double amplitude = accentEveryFour && beat % 4 != 0 ? 0.55 : 0.85;
            for (int index = 0; index < Rate * 0.08 && first + index < audio.Length; index++)
            {
                double time = (double)index / Rate;
                double signal = 0.7 * Math.Sin(2 * Math.PI * 95 * time) + 0.3 * Math.Sin(2 * Math.PI * 1800 * time);
                audio[first + index] += (float)(amplitude * signal * Math.Exp(-time * 65));
            }
        }
        return audio;
    }
}
