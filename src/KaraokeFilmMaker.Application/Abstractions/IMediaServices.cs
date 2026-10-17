using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Abstractions;

public interface IPlaybackClock
{
    ProjectTime Position { get; }

    bool IsPlaying { get; }

    Task PlayAsync(CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);

    Task SeekAsync(ProjectTime position, CancellationToken cancellationToken = default);
}

public interface IAudioPlaybackSession : IPlaybackClock, IAsyncDisposable
{
    bool IsLoaded { get; }

    ProjectDuration Duration { get; }

    string? SourcePath { get; }

    Task LoadAsync(string filePath, CancellationToken cancellationToken = default);
}

public interface IVideoPreviewBackend
{
    Task PresentAsync(ProjectTime projectTime, CancellationToken cancellationToken = default);
}

public interface ISubtitleFrameRenderer
{
    ValueTask<RenderedFrame> RenderAsync(
        ProjectTime projectTime,
        int width,
        int height,
        CancellationToken cancellationToken = default);
}

/// <summary>Top-down BGRA pixels with straight alpha, suitable for a media encoder boundary.</summary>
public sealed record RenderedFrame(int Width, int Height, int Stride, ReadOnlyMemory<byte> BgraPixels);

public interface IMediaTool
{
    Task<MediaProbeResult> ProbeAsync(string filePath, CancellationToken cancellationToken = default);
}

public sealed record MediaProbeResult(ProjectDuration Duration, int? Width, int? Height, double? FrameRate, bool? HasAudio = null);
