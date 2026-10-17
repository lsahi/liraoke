using KaraokeFilmMaker.App.Dialogs;
using Microsoft.Win32;

namespace KaraokeFilmMaker.App.Tests;

public sealed class DialogLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "KaraokeFilmMaker.DialogTests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_root, "settings", "dialog-locations.json");

    [Fact]
    public void CreateInAImportFromBThenRestartOpensAAndPreservesEveryButtonsOwnLocation()
    {
        string projectA = Folder("项目 A");
        string audioB = Folder("歌曲 B");
        var locations = new DialogLocationService(SettingsPath);
        locations.Remember(DialogLocation.NewWorkspace, projectA);
        locations.SeedOpenWorkspace(projectA);
        locations.Remember(DialogLocation.ImportAudio, audioB);

        locations = new DialogLocationService(SettingsPath);
        Assert.Equal(projectA, locations.GetInitialDirectory(DialogLocation.OpenWorkspace));
        Assert.Equal(audioB, locations.GetInitialDirectory(DialogLocation.ImportAudio));

        var expected = Enum.GetValues<DialogLocation>().ToDictionary(location => location, location => Folder(location.ToString()));
        foreach (var (location, directory) in expected) { locations.Remember(location, directory); }
        // Creating another project must not overwrite an explicit Open choice.
        locations.SeedOpenWorkspace(projectA);
        locations = new DialogLocationService(SettingsPath);
        var shellIds = new HashSet<Guid?>();
        foreach (var (location, directory) in expected)
        {
            var dialog = new OpenFileDialog();
            locations.Configure(dialog, location, projectA);
            Assert.Equal(directory, dialog.InitialDirectory);
            Assert.Equal(directory, dialog.DefaultDirectory);
            Assert.True(dialog.RestoreDirectory);
            Assert.True(shellIds.Add(dialog.ClientGuid));
            var afterRestart = new OpenFileDialog();
            new DialogLocationService(SettingsPath).Configure(afterRestart, location);
            Assert.Equal(dialog.ClientGuid, afterRestart.ClientGuid);
        }
    }

    [Fact]
    public void UnusedAndMissingLocationsDoNotBorrowTheAudioDirectory()
    {
        string audio = Folder("audio");
        string export = Folder("workspace/exports");
        string remembered = Folder("moved/video");
        var locations = new DialogLocationService(SettingsPath);
        locations.Remember(DialogLocation.ImportAudio, audio);
        Assert.NotEqual(audio, locations.GetInitialDirectory(DialogLocation.ImportLyrics));
        Assert.Equal(export, locations.GetInitialDirectory(DialogLocation.ExportVideo, export));

        locations.Remember(DialogLocation.ImportVideo, remembered);
        Directory.Delete(remembered);
        locations = new DialogLocationService(SettingsPath);
        Assert.Equal(Path.GetDirectoryName(remembered), locations.GetInitialDirectory(DialogLocation.ImportVideo));
        string customExport = Folder("finished videos");
        locations.Remember(DialogLocation.ExportVideo, customExport);
        Assert.Equal(customExport, new DialogLocationService(SettingsPath).GetInitialDirectory(DialogLocation.ExportVideo, export));
    }

    [Fact]
    public void DamagedOrUnwritablePreferencesDoNotBlockFileSelection()
    {
        string audio = Folder("audio");
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "{broken json");
        var locations = new DialogLocationService(SettingsPath);
        Assert.Equal(audio, locations.GetInitialDirectory(DialogLocation.ImportAudio, audio));
        locations.Remember(DialogLocation.ImportAudio, audio);
        Assert.Equal(audio, new DialogLocationService(SettingsPath).GetInitialDirectory(DialogLocation.ImportAudio));

        string directoryInsteadOfFile = Folder("cannot-write-settings-here.json");
        locations = new DialogLocationService(directoryInsteadOfFile);
        locations.Remember(DialogLocation.ImportAudio, audio);
        Assert.Equal(audio, locations.GetInitialDirectory(DialogLocation.ImportAudio));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
