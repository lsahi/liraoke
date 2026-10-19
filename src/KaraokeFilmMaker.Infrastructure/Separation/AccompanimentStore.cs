using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Workspaces;

namespace KaraokeFilmMaker.Infrastructure.Separation;

public sealed class AccompanimentStore(MediaToolchain tools)
{
    public static string Resolve(string root, Accompaniment item) => WorkspacePaths.AssetPath(root,
        new MediaAsset(item.SourceAssetId, "关联伴奏", item.RelativePath, MediaKind.Audio, item.Duration));

    public static bool IsAvailable(string root, KaraokeProject project)
    {
        if (project.GetAccompaniment(project.MasterAudioAssetId) is not { } item) return false;
        try { return new FileInfo(Resolve(root, item)).Length > 44; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public async Task<KaraokeProject> RegisterAsync(string root, KaraokeProject project, Guid sourceId,
        string generatedPath, CancellationToken cancellationToken = default)
    {
        var source = project.MediaAssets.Single(asset => asset.Id == sourceId);
        var metadata = await new FfprobeMediaReader(tools).ReadDetailsAsync(generatedPath, cancellationToken);
        if (!metadata.HasAudio || Math.Abs(metadata.Duration.TotalSeconds - source.Duration.TotalSeconds) > 0.1)
            throw new InvalidDataException("伴奏时长与原歌曲不一致，未更新关联。");
        string relative = $"accompaniments/{Guid.NewGuid():N}.wav";
        string output = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            await using (var input = File.OpenRead(generatedPath))
            await using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(target, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var mapping = new Accompaniment(source.Id, relative, source.Duration, source.ContentHash, DateTimeOffset.UtcNow);
            return project with
            {
                Accompaniments = [.. project.Accompaniments.Where(item => item.SourceAssetId != sourceId), mapping],
                ModifiedAtUtc = DateTimeOffset.UtcNow,
            };
        }
        catch { if (File.Exists(output)) File.Delete(output); throw; }
    }
}
