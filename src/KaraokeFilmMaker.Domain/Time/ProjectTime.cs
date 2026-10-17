namespace KaraokeFilmMaker.Domain.Time;

/// <summary>
/// An absolute position on the project timeline, stored as integer microseconds.
/// It is deliberately independent from the preview or export frame rate.
/// </summary>
public readonly record struct ProjectTime : IComparable<ProjectTime>
{
    public static readonly ProjectTime Zero = new(0);

    [System.Text.Json.Serialization.JsonConstructor]
    public ProjectTime(long microseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(microseconds);
        Microseconds = microseconds;
    }

    public long Microseconds { get; }

    public double TotalMilliseconds => Microseconds / 1_000d;

    public double TotalSeconds => Microseconds / 1_000_000d;

    public static ProjectTime FromMilliseconds(long milliseconds) =>
        new(checked(milliseconds * 1_000));

    public static ProjectTime FromSeconds(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        return new(checked((long)Math.Round(seconds * 1_000_000d)));
    }

    public int CompareTo(ProjectTime other) => Microseconds.CompareTo(other.Microseconds);

    public static ProjectTime operator +(ProjectTime left, ProjectDuration right) =>
        new(checked(left.Microseconds + right.Microseconds));

    public static ProjectDuration operator -(ProjectTime left, ProjectTime right) =>
        new(checked(left.Microseconds - right.Microseconds));

    public static bool operator <(ProjectTime left, ProjectTime right) => left.CompareTo(right) < 0;

    public static bool operator >(ProjectTime left, ProjectTime right) => left.CompareTo(right) > 0;

    public static bool operator <=(ProjectTime left, ProjectTime right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ProjectTime left, ProjectTime right) => left.CompareTo(right) >= 0;
}
