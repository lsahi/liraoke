using System.IO.Compression;

namespace KaraokeFilmMaker.Infrastructure.Separation;

public static class SafeRuntimeArchive
{
    public static bool IsBuildOnly(string relative)
    {
        string path = relative.Replace('\\', '/');
        return path.StartsWith("torch/", StringComparison.OrdinalIgnoreCase)
            && (path.EndsWith(".lib", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("torch/include/", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task ExtractWheelAsync(string wheel, string sitePackages, CancellationToken token)
    {
        string root = Path.GetFullPath(sitePackages) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using var archive = ZipFile.OpenRead(wheel);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string destination = Path.GetFullPath(Path.Combine(root, relative));
            int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase) || relative.Contains(':')
                || unixType == 0xa000 || entry.Length < 0 || (expanded += entry.Length) > 10L * 1024 * 1024 * 1024)
                throw new InvalidDataException("组件压缩包包含不安全路径或异常大小。");
            if (entry.Name.Length == 0 || IsBuildOnly(entry.FullName)) continue;
            // This pinned Torch wheel installs directly in site-packages, with no .data relocation.
            string first = entry.FullName.Replace('\\', '/').Split('/')[0];
            if (!(first is "torch" or "torchgen" or "functorch" || first.StartsWith("torch-", StringComparison.Ordinal)))
                throw new InvalidDataException("GPU wheel 布局与验证版本不一致。");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        }
    }
}
