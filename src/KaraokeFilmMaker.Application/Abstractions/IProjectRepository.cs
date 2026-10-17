using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Application.Abstractions;

public interface IProjectRepository
{
    Task<KaraokeProject> LoadAsync(string workspacePath, CancellationToken cancellationToken = default);

    Task SaveAsync(string workspacePath, KaraokeProject project, CancellationToken cancellationToken = default);
}
