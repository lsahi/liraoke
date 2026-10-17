namespace KaraokeFilmMaker.Domain.Time;

public readonly record struct TimeRange
{
    [System.Text.Json.Serialization.JsonConstructor]
    public TimeRange(ProjectTime start, ProjectTime end)
    {
        if (end < start)
        {
            throw new ArgumentException("End time cannot be before start time.", nameof(end));
        }

        Start = start;
        End = end;
    }

    public ProjectTime Start { get; }

    public ProjectTime End { get; }

    public ProjectDuration Duration => new(End.Microseconds - Start.Microseconds);

    public bool Contains(ProjectTime time) => time >= Start && time <= End;
}
