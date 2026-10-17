namespace KaraokeFilmMaker.Infrastructure.Workspaces;

/// <summary>The project file lives beside media/ so all persisted asset paths remain portable.</summary>
public static class WorkspaceProjectPath
{
    public static bool IsProjectFile(string path) =>
        Path.GetExtension(path).Equals(".lrn", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".kfp", StringComparison.OrdinalIgnoreCase);

    public static string Resolve(string fileOrDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileOrDirectory);
        string fullPath = Path.GetFullPath(fileOrDirectory);
        if (!Directory.Exists(fullPath) && IsProjectFile(fullPath)) { return fullPath; }
        if (File.Exists(fullPath)) { throw new InvalidDataException("请选择 Liraoke 项目文件（.lrn）。"); }
        if (Directory.Exists(fullPath))
        {
            string[] entries = Directory.EnumerateFiles(fullPath, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path).Equals(".lrn", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (entries.Length == 1) { return entries[0]; }
            if (entries.Length > 1) { throw new InvalidDataException("此文件夹包含多个 .lrn 文件，请直接选择需要打开的项目文件。"); }
        }
        return Path.Combine(fullPath, "project.kfp");
    }

    public static string CreateEntry(string rootPath, string projectTitle)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string name = new(projectTitle.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        name = name.Trim().TrimEnd('.', ' ');
        if (name.Length > 100) { name = name[..(char.IsHighSurrogate(name[99]) ? 99 : 100)].TrimEnd('.', ' '); }
        if (string.IsNullOrWhiteSpace(name)) { name = "Liraoke"; }
        string stem = name.Split('.')[0];
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        if (reserved.Contains(stem, StringComparer.OrdinalIgnoreCase)) { name = "Liraoke_" + name; }
        return Path.Combine(Path.GetFullPath(rootPath), name + ".lrn");
    }
}
