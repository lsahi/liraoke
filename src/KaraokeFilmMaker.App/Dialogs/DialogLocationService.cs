using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace KaraokeFilmMaker.App.Dialogs;

public enum DialogLocation
{
    NewWorkspace,
    OpenWorkspace,
    ImportAudio,
    ImportVideo,
    ImportLyrics,
    ExportVideo,
    ImportLanguagePlugin,
    ImportImage,
}

/// <summary>Per-purpose locations belong to the Windows user, not to an individual project.</summary>
public sealed class DialogLocationService
{
    private readonly string _settingsPath;
    private readonly Dictionary<string, string> _directories;

    public DialogLocationService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KaraokeFilmMaker", "dialog-locations.json");
        try
        {
            _directories = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_settingsPath)) ?? [];
        }
        catch (Exception exception) when (IsSettingsError(exception))
        {
            _directories = [];
        }
    }

    public bool ShowDialog(CommonItemDialog dialog, DialogLocation location, Window owner, string? fallback = null)
    {
        Configure(dialog, location, fallback);
        if (dialog.ShowDialog(owner) != true) { return false; }

        string? directory = dialog switch
        {
            OpenFolderDialog folder => folder.FolderName,
            FileDialog file => Path.GetDirectoryName(file.FileName),
            _ => null,
        };
        Remember(location, directory);
        return true;
    }

    public void Configure(CommonItemDialog dialog, DialogLocation location, string? fallback = null)
    {
        // Stable IDs also isolate the shell's own history (including view preferences).
        dialog.ClientGuid = location switch
        {
            DialogLocation.NewWorkspace => new Guid("d764b77c-6a9c-47e1-bbe8-64a3fb320001"),
            DialogLocation.OpenWorkspace => new Guid("d764b77c-6a9c-47e1-bbe8-64a3fb320002"),
            DialogLocation.ImportAudio => new Guid("d764b77c-6a9c-47e1-bbe8-64a3fb320003"),
            DialogLocation.ImportVideo => new Guid("d764b77c-6a9c-47e1-bbe8-64a3fb320004"),
            DialogLocation.ImportLyrics => new Guid("d764b77c-6a9c-47e1-bbe8-64a3fb320005"),
            DialogLocation.ExportVideo => new Guid("d764b77c-6a9c-47e1-bbe8-64a3fb320006"),
            DialogLocation.ImportLanguagePlugin => new Guid("d764b77c-6a9c-47e1-bbe8-64a3fb320007"),
            DialogLocation.ImportImage => new Guid("d764b77c-6a9c-47e1-bbe8-64a3fb320008"),
            _ => throw new ArgumentOutOfRangeException(nameof(location)),
        };
        // Explicitly set the initial folder so another dialog's shell history cannot win.
        dialog.InitialDirectory = GetInitialDirectory(location, fallback);
        dialog.DefaultDirectory = dialog.InitialDirectory;
        if (dialog is FileDialog file) { file.RestoreDirectory = true; }
    }

    public string GetInitialDirectory(DialogLocation location, string? fallback = null)
    {
        _directories.TryGetValue(location.ToString(), out string? remembered);
        return ExistingDirectoryOrParent(remembered)
            ?? ExistingDirectoryOrParent(fallback)
            ?? ExistingDirectoryOrParent(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public void Remember(DialogLocation location, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) { return; }
        _directories[location.ToString()] = Path.GetFullPath(directory);
        Save();
    }

    public void SeedOpenWorkspace(string directory)
    {
        // A newly created project is useful on the first Open, but never replaces Open's own choice.
        if (!_directories.ContainsKey(nameof(DialogLocation.OpenWorkspace)))
        {
            Remember(DialogLocation.OpenWorkspace, directory);
        }
    }

    private static string? ExistingDirectoryOrParent(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) { return null; }
        try
        {
            directory = Path.GetFullPath(directory);
            while (directory is not null)
            {
                if (Directory.Exists(directory)) { return directory; }
                directory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));
            }
        }
        catch (Exception exception) when (IsSettingsError(exception)) { }
        return null;
    }

    private void Save()
    {
        string temporaryPath = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_directories, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        catch (Exception exception) when (IsSettingsError(exception))
        {
            // A read-only settings directory must not prevent selecting or importing a file.
            System.Diagnostics.Trace.TraceWarning("Could not save dialog locations: {0}", exception.Message);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (IsSettingsError(exception)) { }
        }
    }

    private static bool IsSettingsError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
}
