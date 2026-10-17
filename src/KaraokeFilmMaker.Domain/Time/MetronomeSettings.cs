namespace KaraokeFilmMaker.Domain.Time;

/// <summary>One project-wide beat grid starting at OffsetMilliseconds, with no beats before that anchor.</summary>
public sealed record MetronomeSettings(
    bool Enabled = false,
    double BeatsPerMinute = 120,
    int OffsetMilliseconds = 0)
{
    /// <summary>Timing snap points per beat; does not change the audible click tempo.</summary>
    public int SnapDivisions { get; init; } = 1;

    public const double MinimumBeatsPerMinute = 20;
    public const double MaximumBeatsPerMinute = 400;
    public const int MaximumAbsoluteOffsetMilliseconds = 600_000;

    public void Validate()
    {
        if (SnapDivisions is not (1 or 2 or 3 or 4 or 6 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(SnapDivisions), "吸附细分必须是每拍 1、2、3、4、6 或 8 格。");
        }
        if (!double.IsFinite(BeatsPerMinute)
            || BeatsPerMinute < MinimumBeatsPerMinute || BeatsPerMinute > MaximumBeatsPerMinute)
        {
            throw new ArgumentOutOfRangeException(nameof(BeatsPerMinute), "BPM 必须在 20 到 400 之间。");
        }
        if (OffsetMilliseconds < -MaximumAbsoluteOffsetMilliseconds
            || OffsetMilliseconds > MaximumAbsoluteOffsetMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(OffsetMilliseconds), "节拍偏移必须在 -600000 到 600000 毫秒之间。");
        }
    }
}
