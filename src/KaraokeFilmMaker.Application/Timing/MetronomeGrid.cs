using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Timing;

/// <summary>Deterministic beat math, independent of playback/rendering frame rates.</summary>
public static class MetronomeGrid
{
    public static ProjectTime Snap(ProjectTime input, MetronomeSettings settings, ProjectDuration? duration = null)
    {
        if (!TrySnap(input, settings, duration ?? new ProjectDuration(long.MaxValue), out ProjectTime snapped))
        {
            throw new InvalidOperationException("当前歌曲时长内没有可用的节拍，请调整 BPM 或偏移。");
        }
        return snapped;
    }

    public static bool TrySnap(ProjectTime input, MetronomeSettings settings, ProjectDuration duration,
        out ProjectTime snapped)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (!settings.Enabled)
        {
            snapped = new ProjectTime(Math.Min(input.Microseconds, duration.Microseconds));
            return true;
        }

        // Decimal arithmetic keeps e.g. 120 BPM midpoint ties and signed offsets exact.
        decimal period = 60_000_000m / (decimal)settings.BeatsPerMinute / settings.SnapDivisions;
        decimal offset = settings.OffsetMilliseconds * 1_000m;
        decimal firstBeat = Math.Max(0, decimal.Ceiling(-offset / period));
        decimal lastBeat = decimal.Floor((duration.Microseconds - offset) / period);
        if (firstBeat > lastBeat)
        {
            snapped = ProjectTime.Zero;
            return false;
        }

        decimal nearestBeat = decimal.Floor((input.Microseconds - offset) / period + 0.5m);
        decimal beat = Math.Clamp(nearestBeat, firstBeat, lastBeat);
        long microseconds = (long)decimal.Round(offset + beat * period, 0, MidpointRounding.AwayFromZero);
        snapped = new ProjectTime(Math.Clamp(microseconds, 0, duration.Microseconds));
        return true;
    }
}
