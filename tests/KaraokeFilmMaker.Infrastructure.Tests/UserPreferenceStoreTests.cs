using System.Text.Json;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class UserPreferenceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Liraoke.PreferencesTests", Guid.NewGuid().ToString("N"));
    private static SubtitleStyle Style => KaraokeProject.Create("style", DateTimeOffset.UnixEpoch).SubtitleStyle;

    [Fact]
    public void StylesSurviveRestartOverwriteAndDeletionWithFullParametersAndStableIds()
    {
        var store = new UserPreferenceStore(_root);
        Assert.Empty(store.Load().SubtitleStyles);
        Assert.False(Directory.Exists(_root));
        var style = Style with { PrimaryFontSize = 200, AnnotationFontSize = 56, ShowRomanization = true, MarginRatio = .02 };
        var first = Assert.Single(store.SaveStyle(" My style ", style).SubtitleStyles);
        Assert.Equal("My style", first.Name);
        Assert.Equal(style, Assert.Single(new UserPreferenceStore(_root).Load().SubtitleStyles).Style);
        Assert.Throws<InvalidOperationException>(() => store.SaveStyle("MY STYLE", Style));
        var replaced = Assert.Single(store.SaveStyle("MY STYLE", Style, overwrite: true).SubtitleStyles);
        Assert.Equal(first.Id, replaced.Id);
        Assert.Equal(Style, replaced.Style);
        Assert.Contains("My style", File.ReadAllText(store.FilePath + ".bak"));
        Assert.Empty(store.DeleteStyle(first.Id).SubtitleStyles);
        Assert.Empty(new UserPreferenceStore(_root).Load().SubtitleStyles);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void UpdatesReloadLatestFileAndPreserveOtherPreferencesAndFutureConnectionMetadata()
    {
        Directory.CreateDirectory(_root);
        var first = new UserPreferenceStore(_root);
        var second = new UserPreferenceStore(_root);
        File.WriteAllText(first.FilePath, """
            {"schemaVersion":1,"subtitleStyles":[],"theme":{"mode":"light"},
             "apiConnections":{"future":{"endpoint":"https://example.invalid","model":"test",
              "credentialReference":"windows-vault:future","timeout":15}}}
            """);
        first.SaveStyle("one", Style);
        second.SaveStyle("two", Style);
        Assert.Equal(2, first.Load().SubtitleStyles.Count);
        using var json = JsonDocument.Parse(File.ReadAllText(first.FilePath));
        Assert.Equal("light", json.RootElement.GetProperty("theme").GetProperty("mode").GetString());
        var connection = json.RootElement.GetProperty("apiConnections").GetProperty("future");
        Assert.Equal(15, connection.GetProperty("timeout").GetInt32());
        Assert.Equal("windows-vault:future", connection.GetProperty("credentialReference").GetString());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"schemaVersion":99}""")]
    [InlineData("""{"schemaVersion":1,"subtitleStyles":null}""")]
    [InlineData("""{"schemaVersion":1,"subtitleStyles":[null]}""")]
    public void InvalidOrFutureFileIsNeverOverwritten(string original)
    {
        Directory.CreateDirectory(_root);
        var store = new UserPreferenceStore(_root);
        File.WriteAllText(store.FilePath, original);
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidDataException>(() => store.SaveStyle("valid", Style));
        Assert.Equal(original, File.ReadAllText(store.FilePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    public void InvalidNamesCannotReplaceGoodPreferences(string name)
    {
        var store = new UserPreferenceStore(_root);
        store.SaveStyle("good", Style);
        string original = File.ReadAllText(store.FilePath);
        Assert.Throws<InvalidDataException>(() => store.SaveStyle(name, Style));
        Assert.Throws<InvalidDataException>(() => store.SaveStyle("bad size", Style with { PrimaryFontSize = double.NaN }));
        Assert.Equal(original, File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void LockedFileFailsWithoutLosingSavedStyles()
    {
        var store = new UserPreferenceStore(_root);
        store.SaveStyle("good", Style);
        using (var held = File.Open(store.FilePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => store.SaveStyle("second", Style));
        Assert.Single(store.Load().SubtitleStyles);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
