using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Domain.Media;

public enum MediaKind
{
    Audio,
    Video,
    Image,
}

public sealed record MediaAsset(
    Guid Id,
    string DisplayName,
    string RelativePath,
    MediaKind Kind,
    ProjectDuration Duration,
    string? ContentHash = null);
