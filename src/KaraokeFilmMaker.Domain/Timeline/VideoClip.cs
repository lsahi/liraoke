using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Domain.Timeline;

public sealed record VideoClip(
    Guid Id,
    Guid MediaAssetId,
    ProjectTime TimelineStart,
    ProjectTime SourceStart,
    ProjectDuration Duration,
    bool IsMuted,
    int TrackIndex = 0)
{
    public ProjectTime TimelineEnd => TimelineStart + Duration;
}
