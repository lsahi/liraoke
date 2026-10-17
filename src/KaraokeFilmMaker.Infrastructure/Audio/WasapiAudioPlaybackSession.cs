using System.Runtime.Versioning;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Time;
using NAudio.Wave;

namespace KaraokeFilmMaker.Infrastructure.Audio;

/// <summary>
/// Proof-of-concept audio transport whose reported position comes from the
/// WASAPI device clock rather than a UI stopwatch or source read-ahead position.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed class WasapiAudioPlaybackSession : IAudioPlaybackSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MediaFoundationReader? _reader;
    private WasapiPlayer? _player;
    private ProjectTime _positionAnchor = ProjectTime.Zero;
    private MetronomeSettings _metronome = new();
    private double _projectRate = 1;
    private bool _disposed;

    public bool IsLoaded => _reader is not null && _player is not null;

    public bool IsPlaying => _player?.PlaybackState == PlaybackState.Playing;

    public ProjectDuration Duration => _reader is null
        ? ProjectDuration.Zero
        : ProjectDuration.FromSeconds(_reader.TotalTime.TotalSeconds);

    public string? SourcePath { get; private set; }

    public ProjectTime Position
    {
        get
        {
            if (_player is null || _reader is null)
            {
                return ProjectTime.Zero;
            }

            long renderedBytes = _player.GetPosition();
            double renderedSeconds = (double)renderedBytes / _player.OutputWaveFormat.AverageBytesPerSecond;
            long position = _positionAnchor.Microseconds
                + ProjectDuration.FromSeconds(renderedSeconds).Microseconds;
            return new ProjectTime(Math.Min(position, Duration.Microseconds));
        }
    }

    public async Task LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string fullPath = Path.GetFullPath(filePath);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await DisposePlaybackObjectsAsync().ConfigureAwait(false);

            _reader = new MediaFoundationReader(fullPath);
            _player = CreatePlayer(_reader, ProjectTime.Zero);
            _positionAnchor = ProjectTime.Zero;
            SourcePath = fullPath;
        }
        catch
        {
            await DisposePlaybackObjectsAsync().ConfigureAwait(false);
            SourcePath = null;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task PlayAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureLoaded();
        _player!.Play();
        return Task.CompletedTask;
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureLoaded();
            if (!IsPlaying) { return; }
            ProjectTime frozen = Position;
            // Recreate the paused output at the rendered position. A device can
            // continue reporting buffered frames briefly after a lightweight Pause.
            await _player!.DisposeAsync().ConfigureAwait(false);
            _reader!.CurrentTime = TimeSpan.FromSeconds(frozen.TotalSeconds);
            _player = CreatePlayer(_reader, frozen);
            _positionAnchor = frozen;
        }
        finally { _gate.Release(); }
    }

    public async Task SeekAsync(ProjectTime position, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureLoaded();

            ProjectTime target = position > new ProjectTime(Duration.Microseconds)
                ? new ProjectTime(Duration.Microseconds)
                : position;
            bool resume = IsPlaying;

            await _player!.DisposeAsync().ConfigureAwait(false);
            _reader!.CurrentTime = TimeSpan.FromSeconds(target.TotalSeconds);
            _player = CreatePlayer(_reader, target);
            _positionAnchor = target;

            if (resume)
            {
                _player.Play();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await DisposePlaybackObjectsAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    /// <summary>Rebuild at the rendered clock position so no old click settings remain buffered.</summary>
    public async Task ConfigureMetronomeAsync(MetronomeSettings settings, double projectRate = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (!double.IsFinite(projectRate) || projectRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(projectRate));
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_metronome == settings && _projectRate == projectRate) { return; }
            bool loaded = IsLoaded;
            bool resume = IsPlaying;
            ProjectTime frozen = Position;
            _metronome = settings;
            _projectRate = projectRate;
            if (!loaded) { return; }
            await _player!.DisposeAsync().ConfigureAwait(false);
            _player = null;
            _reader!.CurrentTime = TimeSpan.FromSeconds(frozen.TotalSeconds);
            _player = CreatePlayer(_reader, frozen);
            _positionAnchor = frozen;
            if (resume) { _player.Play(); }
        }
        finally { _gate.Release(); }
    }

    private WasapiPlayer CreatePlayer(IWaveProvider source, ProjectTime physicalStart)
    {
        if (_metronome.Enabled)
        {
            source = new MetronomeSampleProvider(source.ToSampleProvider(), _metronome,
                physicalStart.TotalSeconds, _projectRate).ToWaveProvider();
        }
        var player = new WasapiPlayerBuilder()
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(50)
            .WithMmcssThreadPriority("Pro Audio")
            .Build();
        player.Init(source);
        return player;
    }

    private async Task DisposePlaybackObjectsAsync()
    {
        if (_player is not null)
        {
            await _player.DisposeAsync().ConfigureAwait(false);
            _player = null;
        }

        _reader?.Dispose();
        _reader = null;
        _positionAnchor = ProjectTime.Zero;
    }

    private void EnsureLoaded()
    {
        if (!IsLoaded)
        {
            throw new InvalidOperationException("Load an audio file before controlling playback.");
        }
    }
}
