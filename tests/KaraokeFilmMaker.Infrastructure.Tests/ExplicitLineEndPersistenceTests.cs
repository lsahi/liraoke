using System.Text.Json.Nodes;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class ExplicitLineEndPersistenceTests
{
    [Fact]
    public async Task ExplicitEndRoundTripsAndOldProjectsNeverInferEnter()
    {
        string root = Path.Combine(Path.GetTempPath(), "Liraoke.Overlap", Guid.NewGuid().ToString("N"));
        try
        {
            ProjectTime At(double s) => ProjectTime.FromSeconds(s);
            var lyrics = new LyricImportService().Parse("Hello\nWorld", ".txt", LyricLanguage.English);
            lyrics = new LyricTimingService().ApplyContinuousUnitMarkers(lyrics, [At(1)], At(10), 0, At(8), explicitLineEnd: true);
            var project = KaraokeProject.Create("Overlap", DateTimeOffset.UnixEpoch) with { Lyrics = lyrics };
            var repository = new JsonProjectRepository();
            await repository.SaveAsync(root, project);
            var loaded = await repository.LoadAsync(root);
            Assert.Equal(At(8), loaded.Lyrics.Lines[0].ExplicitEndTime);
            Assert.Equal(lyrics.Lines[0].Units[0].Timing, loaded.Lyrics.Lines[0].Units[0].Timing);
            Assert.Equal(lyrics.Lines[0].Units[0].RawInputTime, loaded.Lyrics.Lines[0].Units[0].RawInputTime);
            Assert.True(loaded.Lyrics.Lines[0].Units[0].IsConfirmed);
            var path = Path.Combine(root, "project.kfp");
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            json["schemaVersion"] = 10;
            foreach (var line in json["lyrics"]!["lines"]!.AsArray()) line!.AsObject().Remove("explicitEndTime");
            await File.WriteAllTextAsync(path, json.ToJsonString());
            loaded = await repository.LoadAsync(root);
            Assert.Equal(KaraokeProject.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Null(loaded.Lyrics.Lines[0].ExplicitEndTime);
            Assert.Equal(lyrics.Lines[0].Timing, loaded.Lyrics.Lines[0].Timing);
            Assert.Equal(lyrics.Lines[0].Units[0].Id, loaded.Lyrics.Lines[0].Units[0].Id);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
