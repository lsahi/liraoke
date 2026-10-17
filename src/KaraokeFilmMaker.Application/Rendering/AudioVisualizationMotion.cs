using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Rendering;

public readonly record struct FireworkBurst(int Seed, double StartSeconds, double Strength, double X, double Y);

/// <summary>Stateless choreography in original song time: seeking, cropping and exporting agree.</summary>
public static class AudioVisualizationMotion
{
    public const int DenseBarCount = 128;
    public const int CenterBarCount = 256;
    public const double FireworkLifetime = 1.6;
    public const int MaximumActiveBursts = 9;

    public static double RotationDegrees(ProjectTime time) => time.TotalSeconds % 100 * 3.6;

    // More visual columns, not invented FFT resolution. Reuse the same 32-band cache for every style.
    public static float DenseBand(ReadOnlySpan<float> bands, int index, int count = DenseBarCount)
    {
        if (bands.IsEmpty || count < 2 || index < 0 || index >= count) { throw new ArgumentOutOfRangeException(nameof(index)); }
        double position = (double)index * (bands.Length - 1) / (count - 1);
        int left = (int)position, right = Math.Min(left + 1, bands.Length - 1);
        return bands[left] + (bands[right] - bands[left]) * (float)(position - left);
    }

    public static int SampleFireworks(AudioVisualizationData data, ProjectTime time, Span<FireworkBurst> destination)
    {
        if (destination.Length < MaximumActiveBursts) { throw new ArgumentException("Insufficient burst slots.", nameof(destination)); }
        double now = time.TotalSeconds;
        if (now >= data.DurationSeconds) { return 0; }
        int last = (int)Math.Floor(now * 5), count = 0;
        Span<float> bands = stackalloc float[AudioVisualizationData.SpectrumBandCount];
        // At most nine local samples; no whole-song scans, random generator state or allocations per frame.
        for (int tick = Math.Max(0, last - 8); tick <= last; tick++)
        {
            double start = tick / 5.0, age = now - start;
            if (age >= FireworkLifetime) { continue; }
            data.Sample(ProjectTime.FromSeconds(start), bands);
            double strength = LowMidEnergy(bands);
            if (strength < 0.18) { continue; }
            double previous = 0;
            if (tick > 0)
            {
                data.Sample(ProjectTime.FromSeconds(start - 0.1), bands);
                previous = LowMidEnergy(bands);
            }
            // Accents trigger immediately; sustained low/mid energy produces sparse scattered bursts.
            if (strength - previous < 0.08 && Noise(tick, 0) >= strength * 0.30) { continue; }
            destination[count++] = new(tick, start, strength, 0.12 + Noise(tick, 1) * 0.76, 0.12 + Noise(tick, 2) * 0.68);
        }
        return count;
    }

    private static double LowMidEnergy(ReadOnlySpan<float> bands)
    {
        // The cached log-frequency bands 0..19 cover approximately 40..818 Hz.
        double square = 0, peak = 0;
        for (int i = 0; i < 20; i++) { square += bands[i] * bands[i]; peak = Math.Max(peak, bands[i]); }
        return Math.Sqrt(square / 20) * 0.65 + peak * 0.35;
    }

    public static double Noise(int seed, int channel)
    {
        uint value = unchecked((uint)seed * 747796405u + (uint)channel * 2891336453u + 277803737u);
        value = unchecked(((value >> (int)((value >> 28) + 4)) ^ value) * 277803737u);
        return ((value >> 22) ^ value) / (double)uint.MaxValue;
    }
}
