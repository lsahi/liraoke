using KaraokeFilmMaker.Domain.Media;

namespace KaraokeFilmMaker.Infrastructure.Workspaces;

public static class WorkspacePaths
{
    public static string AssetPath(string rootPath, MediaAsset asset)
    {
        string root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("素材路径超出了工作区。");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"工作区素材丢失：{asset.DisplayName}", path);
        }

        return path;
    }
}
