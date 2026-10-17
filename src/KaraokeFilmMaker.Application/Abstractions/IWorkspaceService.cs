using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Application.Abstractions;

public sealed record WorkspaceSession(string RootPath, KaraokeProject Project, string? ProjectFilePath = null);

public interface IWorkspaceService
{
    Task<WorkspaceSession> CreateAsync(
        string workspacePath,
        string projectTitle,
        CancellationToken cancellationToken = default);

    Task<WorkspaceSession> OpenAsync(
        string workspacePath,
        CancellationToken cancellationToken = default);

    Task<WorkspaceSession> SaveAsync(
        WorkspaceSession session,
        CancellationToken cancellationToken = default);

    Task<WorkspaceSession> ImportMediaAsync(
        WorkspaceSession session,
        string sourcePath,
        MediaKind kind,
        CancellationToken cancellationToken = default);
}

public interface IMediaMetadataReader
{
    Task<MediaProbeResult> ReadAsync(
        string filePath,
        MediaKind kind,
        CancellationToken cancellationToken = default);
}
