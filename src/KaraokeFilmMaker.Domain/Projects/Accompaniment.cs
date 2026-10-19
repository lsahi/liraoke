using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Domain.Projects;

public sealed record Accompaniment(Guid SourceAssetId, string RelativePath, ProjectDuration Duration,
    string? SourceContentHash, DateTimeOffset CreatedAtUtc);
