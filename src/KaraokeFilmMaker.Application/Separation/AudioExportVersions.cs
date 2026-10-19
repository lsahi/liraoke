namespace KaraokeFilmMaker.Application.Separation;

public sealed record AudioExportVersion(string Path, bool Accompaniment);

public static class AudioExportVersions
{
    public static IReadOnlyList<AudioExportVersion> Create(string selectedPath, bool original, bool accompaniment)
    {
        if (!original && !accompaniment) throw new ArgumentException("请至少选择一个导出版本。");
        string stem = Path.GetFileNameWithoutExtension(selectedPath).TrimEnd();
        foreach (string suffix in new[] { "-on vocal-", "-off vocal-" })
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                stem = stem[..^suffix.Length].TrimEnd();
        if (string.IsNullOrWhiteSpace(stem)) stem = "Liraoke";
        string folder = Path.GetDirectoryName(Path.GetFullPath(selectedPath))!;
        List<AudioExportVersion> versions = [];
        if (original) versions.Add(new(Path.Combine(folder, stem + " -on vocal-.mp4"), false));
        if (accompaniment) versions.Add(new(Path.Combine(folder, stem + " -off vocal-.mp4"), true));
        return versions;
    }
}
