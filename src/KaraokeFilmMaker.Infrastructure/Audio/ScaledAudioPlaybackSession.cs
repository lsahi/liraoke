using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Infrastructure.Audio;

public sealed class ScaledAudioPlaybackSession : IAudioPlaybackSession, IMetronomePlaybackSession
{
    private readonly WasapiAudioPlaybackSession _session = new();
    private double _rate = 1;
    private ProjectDuration _duration;
    private MetronomeSettings _metronome = new();

    public bool IsLoaded => _session.IsLoaded;
    public bool IsPlaying => _session.IsPlaying;
    public ProjectDuration Duration => _duration;
    public string? SourcePath => _session.SourcePath;
    public ProjectTime Position
    {
        get
        {
            ProjectTime physical = _session.Position;
            if (_session.IsLoaded && physical.Microseconds >= _session.Duration.Microseconds)
            {
                return new ProjectTime(_duration.Microseconds);
            }
            return ProjectTime.FromSeconds(Math.Min(_duration.TotalSeconds, physical.TotalSeconds * _rate));
        }
    }

    public async Task LoadPreparedAsync(string path, ProjectDuration originalDuration, double rate,
        CancellationToken cancellationToken = default)
    {
        await _session.ConfigureMetronomeAsync(_metronome, rate, cancellationToken).ConfigureAwait(false);
        await _session.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        _duration = originalDuration;
        _rate = rate;
    }

    public async Task LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await _session.ConfigureMetronomeAsync(_metronome, 1, cancellationToken).ConfigureAwait(false);
        await _session.LoadAsync(filePath, cancellationToken).ConfigureAwait(false);
        _duration = _session.Duration;
        _rate = 1;
    }

    public Task PlayAsync(CancellationToken cancellationToken = default) => _session.PlayAsync(cancellationToken);
    public Task PauseAsync(CancellationToken cancellationToken = default) => _session.PauseAsync(cancellationToken);
    public Task SeekAsync(ProjectTime position, CancellationToken cancellationToken = default) =>
        _session.SeekAsync(ProjectTime.FromSeconds(position.TotalSeconds / _rate), cancellationToken);

    public async Task ConfigureMetronomeAsync(MetronomeSettings settings, CancellationToken cancellationToken = default)
    {
        await _session.ConfigureMetronomeAsync(settings, _rate, cancellationToken).ConfigureAwait(false);
        _metronome = settings;
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
