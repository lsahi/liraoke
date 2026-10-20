using System.Text.Json.Nodes;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class RoleTitlePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Liraoke.Roles", Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task RolesAndTitleRoundTripAndSchemaNineGetsSafeDefaults()
    {
        var p = KaraokeProject.Create("Round trip", DateTimeOffset.UnixEpoch);
        var role = new SingingRole(Guid.NewGuid(), "主唱", p.SubtitleStyle with { ActiveColor = "#FF77CCFF" });
        var lyrics = new LyricImportService().Parse("Hello world", ".txt", LyricLanguage.English);
        var line = lyrics.Lines[0] with { RoleIds = [role.Id] };
        line = line with { Units = line.Units.Select((u, i) => u with { RoleIds = i == 0 ? [] : null }).ToArray() };
        p = p with { Roles = [role], SongTitle = new() { Enabled = true, Text = "My Song", Position = SongTitlePosition.TopRight },
            Lyrics = lyrics with { Lines = [line] } };
        var repo = new JsonProjectRepository();
        await repo.SaveAsync(_root, p);
        var saved = await repo.LoadAsync(_root);
        Assert.Equal(role, Assert.Single(saved.Roles)); Assert.Equal(p.SongTitle, saved.SongTitle);
        Assert.Empty(saved.Lyrics.Lines[0].Units[0].RoleIds!); Assert.Null(saved.Lyrics.Lines[0].Units[1].RoleIds);
        string path = Path.Combine(_root, "project.kfp");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        json["schemaVersion"] = 9; json.Remove("roles"); json.Remove("songTitle");
        foreach (var l in json["lyrics"]!["lines"]!.AsArray())
        { l!.AsObject().Remove("roleIds"); foreach (var u in l["units"]!.AsArray()) u!.AsObject().Remove("roleIds"); }
        await File.WriteAllTextAsync(path, json.ToJsonString());
        var legacy = await repo.LoadAsync(_root);
        Assert.Equal(KaraokeProject.CurrentSchemaVersion, legacy.SchemaVersion);
        Assert.False(legacy.SongTitle.Enabled); Assert.Empty(legacy.Roles);
        Assert.Equal(line.Units.Select(u => u.Id), legacy.Lyrics.Lines[0].Units.Select(u => u.Id));
        Assert.Equal(line.Timing, legacy.Lyrics.Lines[0].Timing);
    }

    [Fact]
    public void RolePresetsAreIndependentCopiesAndPreserveStylePreferences()
    {
        var p = KaraokeProject.Create("Preferences", DateTimeOffset.UnixEpoch);
        var store = new UserPreferenceStore(_root);
        store.SaveStyle("普通", p.SubtitleStyle);
        store.SaveRole("A", p.SubtitleStyle);
        var original = Assert.Single(store.Load().Roles);
        Assert.Throws<InvalidOperationException>(() => store.SaveRole("a", p.SubtitleStyle));
        store.SaveRole("A", p.SubtitleStyle with { PrimaryFontSize = 100 }, true);
        var updated = Assert.Single(new UserPreferenceStore(_root).Load().Roles);
        Assert.Equal(original.Id, updated.Id); Assert.Equal(100, updated.Style.PrimaryFontSize);
        Assert.Equal(64, original.Style.PrimaryFontSize);
        Assert.Single(store.Load().SubtitleStyles);
        store.DeleteRole(updated.Id); Assert.Empty(store.Load().Roles); Assert.Single(store.Load().SubtitleStyles);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
