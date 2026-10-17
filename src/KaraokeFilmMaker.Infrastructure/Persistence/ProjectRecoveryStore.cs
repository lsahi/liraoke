using System.Text.Json;
using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Infrastructure.Persistence;

public sealed class ProjectRecoveryStore
{
    private readonly JsonProjectRepository _repository = new("project.recovery.kfp");

    public Task SaveAsync(string root, KaraokeProject project, CancellationToken cancellationToken = default) =>
        _repository.SaveAsync(root, project with { ModifiedAtUtc = DateTimeOffset.UtcNow }, cancellationToken);

    public async Task<KaraokeProject?> FindAsync(string root, KaraokeProject saved, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path.Combine(root, "project.recovery.kfp")))
        {
            return null;
        }

        KaraokeProject recovery = await _repository.LoadAsync(root, cancellationToken).ConfigureAwait(false);
        return recovery.Id == saved.Id && recovery.ModifiedAtUtc > saved.ModifiedAtUtc
            && Content(recovery) != Content(saved) ? recovery : null;
    }

    public static string Content(KaraokeProject project) =>
        JsonSerializer.Serialize(project with { ModifiedAtUtc = DateTimeOffset.MinValue });
}
