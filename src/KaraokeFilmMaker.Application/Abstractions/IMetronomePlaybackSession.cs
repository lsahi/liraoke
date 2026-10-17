using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Abstractions;

/// <summary>Optional monitor-only transport capability; not part of encoded media.</summary>
public interface IMetronomePlaybackSession
{
    Task ConfigureMetronomeAsync(MetronomeSettings settings, CancellationToken cancellationToken = default);
}
