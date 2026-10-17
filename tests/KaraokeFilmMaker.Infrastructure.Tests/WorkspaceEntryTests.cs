using System.Text.Json;
using System.Text.Json.Nodes;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Media;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Infrastructure.Workspaces;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class WorkspaceEntryTests : IDisposable
{
    [Fact]
    public async Task WorkAreaSurvivesSaveRecoveryAndResetsWhenReplacingOrRemovingSong()
    {
        var session = await _service.CreateAsync(_root, "裁切");
        string source = Path.Combine(_root, "source.wav");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        session = await _service.ImportMediaAsync(session, source, MediaKind.Audio);
        var range = new TimeRange(ProjectTime.FromSeconds(1.234567), ProjectTime.FromSeconds(8.765432));
        session = session with { Project = session.Project with { WorkArea = range } };
        await _service.SaveAsync(session);
        var reopened = await _service.OpenAsync(session.ProjectFilePath!);
        Assert.Equal(range, reopened.Project.WorkArea);
        var recovery = new ProjectRecoveryStore();
        await recovery.SaveAsync(_root, session.Project with { Title = "恢复裁切" });
        Assert.Equal(range, (await recovery.FindAsync(_root, reopened.Project))!.WorkArea);
        var replacement = await _service.ImportMediaAsync(session, source, MediaKind.Audio);
        Assert.Null(replacement.Project.WorkArea);
        Assert.Null(MediaAssetRemoval.Remove(session.Project, session.Project.MasterAudioAssetId!.Value, true).WorkArea);
        Assert.Equal(range, session.Project.WorkArea); // Original snapshot remains undoable.
    }

    [Fact]
    public async Task SchemaSixWithoutWorkAreaRetainsFullSong()
    {
        var session = await _service.CreateAsync(_root, "旧版");
        JsonNode json = JsonNode.Parse(await File.ReadAllTextAsync(session.ProjectFilePath!))!;
        json["schemaVersion"] = 6;
        json.AsObject().Remove("workArea");
        await File.WriteAllTextAsync(session.ProjectFilePath!, json.ToJsonString());
        var reopened = await _service.OpenAsync(session.ProjectFilePath!);
        Assert.Null(reopened.Project.WorkArea);
        Assert.Equal(KaraokeProject.CurrentSchemaVersion, reopened.Project.SchemaVersion);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public async Task SnapDivisionsPersistInWorkspaceAndRecoveryWithoutChangingLyrics(int divisions)
    {
        var session = await _service.CreateAsync(_root, "Subdivision");
        session = session with { Project = session.Project with
        { Metronome = new MetronomeSettings(true, 120, 100) { SnapDivisions = divisions } } };
        await _service.SaveAsync(session);
        var reopened = await _service.OpenAsync(session.ProjectFilePath!);
        Assert.Equal(divisions, reopened.Project.Metronome!.SnapDivisions);
        Assert.Equal(session.Project.Lyrics.Lines, reopened.Project.Lyrics.Lines);
        var recovery = new ProjectRecoveryStore();
        var changed = session.Project with { Title = "Recovery with divisions" };
        await recovery.SaveAsync(_root, changed);
        Assert.Equal(divisions, (await recovery.FindAsync(_root, reopened.Project))!.Metronome!.SnapDivisions);
    }

    [Fact]
    public async Task LegacyMetronomeWithoutDivisionsDefaultsToWholeBeat()
    {
        var session = await _service.CreateAsync(_root, "Legacy beat");
        JsonNode json = JsonNode.Parse(await File.ReadAllTextAsync(session.ProjectFilePath!))!;
        json["schemaVersion"] = 5;
        json["metronome"]!.AsObject().Remove("snapDivisions");
        await File.WriteAllTextAsync(session.ProjectFilePath!, json.ToJsonString());
        var reopened = await _service.OpenAsync(session.ProjectFilePath!);
        Assert.Equal(KaraokeProject.CurrentSchemaVersion, reopened.Project.SchemaVersion);
        Assert.Equal(1, reopened.Project.Metronome!.SnapDivisions);
        Assert.Equal(session.Project.Lyrics.Lines, reopened.Project.Lyrics.Lines);
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "Liraoke.EntryTests", Guid.NewGuid().ToString("N"));
    private readonly WorkspaceService _service = new(new JsonProjectRepository(), new StubMetadata());

    [Fact]
    public async Task NamedEntryOpensDirectlyAndSaveKeepsItsFilenameAfterTitleChanges()
    {
        var created = await _service.CreateAsync(_root, "第一首歌");
        Assert.Equal(Path.Combine(_root, "第一首歌.lrn"), created.ProjectFilePath);
        var opened = await _service.OpenAsync(created.ProjectFilePath!);
        Assert.Equal(created.RootPath, opened.RootPath);
        Assert.Equal(created.Project.Id, opened.Project.Id);
        var metronome = new MetronomeSettings(true, 137.5, -125);
        var changed = await _service.SaveAsync(opened with { Project = opened.Project with { Title = "新标题", Metronome = metronome } });
        Assert.Equal(opened.ProjectFilePath, changed.ProjectFilePath);
        Assert.Equal("新标题", (await _service.OpenAsync(_root)).Project.Title);
        Assert.Equal(metronome, (await _service.OpenAsync(changed.ProjectFilePath!)).Project.Metronome);
        Assert.False(File.Exists(Path.Combine(_root, "project.kfp")));
        Assert.Single(Directory.GetFiles(_root, "*.lrn"));
    }

    [Fact]
    public async Task PortableEntryAndRemovalSaveKeepMediaCopyAndRecovery()
    {
        var session = await _service.CreateAsync(_root, "Portable");
        string source = Path.Combine(_root, "source.mp3");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        session = await _service.ImportMediaAsync(session, source, MediaKind.Audio);
        session = await _service.SaveAsync(session);
        var original = session.Project;
        var asset = original.MediaAssets.Single();
        string copiedPath = Path.Combine(_root, asset.RelativePath);
        var recovery = new ProjectRecoveryStore();
        var removed = MediaAssetRemoval.Remove(original, asset.Id, true);
        await recovery.SaveAsync(_root, removed);
        Assert.Empty((await recovery.FindAsync(_root, original))!.MediaAssets);
        session = await _service.SaveAsync(session with { Project = removed });
        Assert.Empty((await _service.OpenAsync(session.ProjectFilePath!)).Project.MediaAssets);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(copiedPath));
        await _service.SaveAsync(session with { Project = original }); // Undo retains usable media references.
        Assert.Equal(asset, (await _service.OpenAsync(session.ProjectFilePath!)).Project.MediaAssets.Single());
        string relocated = _root + "-moved";
        Directory.Move(_root, relocated);
        try
        {
            var reopened = await _service.OpenAsync(Path.Combine(relocated, "Portable.lrn"));
            Assert.True(File.Exists(Path.Combine(reopened.RootPath, reopened.Project.MediaAssets.Single().RelativePath)));
        }
        finally { Directory.Move(relocated, _root); }
    }

    [Fact]
    public async Task LegacyFolderStillOpensButAmbiguousEntriesRequireExplicitSelection()
    {
        var repository = new JsonProjectRepository();
        var project = KaraokeProject.Create("Legacy", DateTimeOffset.UtcNow);
        await repository.SaveAsync(_root, project);
        Assert.Equal(Path.Combine(_root, "project.kfp"), (await _service.OpenAsync(_root)).ProjectFilePath);
        await repository.SaveAsync(Path.Combine(_root, "one.lrn"), project);
        await repository.SaveAsync(Path.Combine(_root, "two.lrn"), project);
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.OpenAsync(_root));
        Assert.Equal(project.Id, (await _service.OpenAsync(Path.Combine(_root, "two.lrn"))).Project.Id);
    }

    [Theory]
    [InlineData("CON", "Liraoke_CON.lrn")]
    [InlineData("Song:One?", "Song_One_.lrn")]
    [InlineData("歌曲 . ", "歌曲.lrn")]
    public async Task TitleProducesSafeWindowsEntryName(string title, string filename)
    {
        var created = await _service.CreateAsync(_root, title);
        Assert.Equal(filename, Path.GetFileName(created.ProjectFilePath));
        Assert.Equal(title.Trim(), created.Project.Title);
    }

    [Fact]
    public async Task Schema3WithOnlyEmptyExtraTracksNormalizesButActualExtraTrackClipsAreRejected()
    {
        var repository = new JsonProjectRepository();
        var session = await _service.CreateAsync(_root, "Migration");
        JsonNode json = JsonNode.Parse(await File.ReadAllTextAsync(session.ProjectFilePath!))!;
        json["schemaVersion"] = 3;
        json["videoTrackCount"] = 2;
        await File.WriteAllTextAsync(session.ProjectFilePath!, json.ToJsonString());
        var migrated = await repository.LoadAsync(session.ProjectFilePath!);
        Assert.Equal(KaraokeProject.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Equal(1, migrated.VideoTrackCount);
        json["videoClips"] = new JsonArray(new JsonObject
        {
            ["id"] = Guid.NewGuid(), ["mediaAssetId"] = Guid.NewGuid(), ["trackIndex"] = 1,
            ["timelineStart"] = new JsonObject { ["microseconds"] = 0 },
            ["sourceStart"] = new JsonObject { ["microseconds"] = 0 },
            ["duration"] = new JsonObject { ["microseconds"] = 1000000 },
        });
        await File.WriteAllTextAsync(session.ProjectFilePath!, json.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadAsync(session.ProjectFilePath!));
    }

    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
    private sealed class StubMetadata : IMediaMetadataReader
    {
        public Task<MediaProbeResult> ReadAsync(string filePath, MediaKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaProbeResult(ProjectDuration.FromSeconds(10), null, null, null));
    }
}
