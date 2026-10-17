using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Rendering;

/// <summary>A compact, immutable spectrum indexed by original song time, independent of playback speed.</summary>
public sealed class AudioVisualizationData
{
    public const int SpectrumBandCount = 32;
    public const int SpectrumFramesPerSecond = 30;
    public const int MaximumDurationSeconds = 4 * 60 * 60;
    public const int MaximumFrameCount = MaximumDurationSeconds * SpectrumFramesPerSecond;
    private readonly byte[] _frames;

    public AudioVisualizationData(double durationSeconds, ReadOnlySpan<byte> frames)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds < 0 || durationSeconds > MaximumDurationSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }
        if (frames.Length % SpectrumBandCount != 0 || frames.Length > MaximumFrameCount * SpectrumBandCount)
        {
            throw new ArgumentException("频谱帧数据的长度无效。", nameof(frames));
        }
        int count = frames.Length / SpectrumBandCount;
        if (count != (int)Math.Ceiling(durationSeconds * SpectrumFramesPerSecond - 1e-9))
        {
            throw new ArgumentException("频谱帧数与歌曲时长不一致。", nameof(frames));
        }
        DurationSeconds = durationSeconds;
        _frames = frames.ToArray();
    }

    public double DurationSeconds { get; }
    public int FrameCount => _frames.Length / SpectrumBandCount;
    public int BandCount => SpectrumBandCount;
    public int FramesPerSecond => SpectrumFramesPerSecond;

    /// <summary>Samples without allocations. Out-of-song positions and silence yield zeros.</summary>
    public void Sample(ProjectTime time, Span<float> destination)
    {
        if (destination.Length < SpectrumBandCount) { throw new ArgumentException("需要 32 个频谱槽位。", nameof(destination)); }
        Span<float> result = destination[..SpectrumBandCount];
        double seconds = time.TotalSeconds;
        if (FrameCount == 0 || seconds >= DurationSeconds) { result.Clear(); return; }
        double position = seconds * SpectrumFramesPerSecond;
        int first = Math.Min((int)position, FrameCount - 1);
        int second = Math.Min(first + 1, FrameCount - 1);
        float fraction = (float)(position - first);
        float fade = second == first ? (float)Math.Clamp((DurationSeconds - seconds) * SpectrumFramesPerSecond, 0, 1) : 1;
        for (int band = 0; band < SpectrumBandCount; band++)
        {
            float start = _frames[first * SpectrumBandCount + band] / 255f;
            float end = _frames[second * SpectrumBandCount + band] / 255f;
            result[band] = (start + (end - start) * fraction) * fade;
        }
    }
}
