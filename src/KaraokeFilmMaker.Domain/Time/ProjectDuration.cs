namespace KaraokeFilmMaker.Domain.Time;

public readonly record struct ProjectDuration
{
    public static readonly ProjectDuration Zero = new(0);

    [System.Text.Json.Serialization.JsonConstructor]
    public ProjectDuration(long microseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(microseconds);
        Microseconds = microseconds;
    }

    public long Microseconds { get; }

    public double TotalSeconds => Microseconds / 1_000_000d;

    public static ProjectDuration FromMilliseconds(long milliseconds) =>
        new(checked(milliseconds * 1_000));

    public static ProjectDuration FromSeconds(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        return new(checked((long)Math.Round(seconds * 1_000_000d)));
    }
}
