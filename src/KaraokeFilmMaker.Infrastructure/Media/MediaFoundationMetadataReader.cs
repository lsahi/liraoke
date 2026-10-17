using System.Runtime.Versioning;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Time;
using NAudio.Wave;

namespace KaraokeFilmMaker.Infrastructure.Media;

[SupportedOSPlatform("windows10.0.17763")]
public sealed class MediaFoundationMetadataReader : IMediaMetadataReader
{
    public Task<MediaProbeResult> ReadAsync(
        string filePath,
        MediaKind kind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var reader = new MediaFoundationReader(filePath);
            var result = new MediaProbeResult(
                ProjectDuration.FromSeconds(reader.TotalTime.TotalSeconds),
                Width: null,
                Height: null,
                FrameRate: null);
            return Task.FromResult(result);
        }
        catch when (kind == MediaKind.Video)
        {
            // A silent MP4 has no audio stream for MediaFoundationReader. FFprobe will
            // replace this fallback when the export toolchain is introduced.
            return Task.FromResult(new MediaProbeResult(ProjectDuration.Zero, null, null, null));
        }
    }
}
