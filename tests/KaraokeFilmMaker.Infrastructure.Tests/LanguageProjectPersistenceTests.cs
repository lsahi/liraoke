using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class LanguageProjectPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Liraoke.LanguagePersistence", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(1, LyricLanguage.Japanese, "ja")]
    [InlineData(2, LyricLanguage.Japanese, "ja")]
    [InlineData(2, LyricLanguage.Chinese, "zh")]
    [InlineData(2, LyricLanguage.English, "en")]
    public async Task LegacyMigrationOnlyAddsLockedLanguageMetadata(int schema, LyricLanguage language, string id)
    {
        KaraokeProject original = CreateProject(language) with { SchemaVersion = schema };
        Directory.CreateDirectory(_root);
        // Serialize an actual old-format shape, without any new optional model fields.
        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(original, JsonOptions))!.AsObject();
        JsonObject lineJson = json["lyrics"]!["lines"]![0]!.AsObject();
        lineJson.Remove("languageSpans");
        lineJson.Remove("presentation");
        foreach (JsonNode? unitJson in lineJson["units"]!.AsArray()) { unitJson!.AsObject().Remove("languageId"); }
        await File.WriteAllTextAsync(Path.Combine(_root, "project.kfp"), json.ToJsonString());

        var repository = new JsonProjectRepository();
        KaraokeProject migrated = await repository.LoadAsync(_root);
        Assert.Equal(KaraokeProject.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Equal(language, migrated.Lyrics.Language);
        LyricLine before = original.Lyrics.Lines[0];
        LyricLine after = migrated.Lyrics.Lines[0];
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Timing, after.Timing);
        Assert.Equal(before.ReadingText, after.ReadingText);
        Assert.Equal(before.RomanizationText, after.RomanizationText);
        Assert.Equal(before.ReadingSource, after.ReadingSource);
        Assert.Null(after.Presentation);
        Assert.Equal(JsonSerializer.Serialize(before.Units, JsonOptions), JsonSerializer.Serialize(after.Units, JsonOptions));
        LyricLanguageSpan span = Assert.Single(after.LanguageSpans!);
        Assert.Equal(id, span.LanguageId);
        Assert.Equal(new TextRange(0, before.PrimaryText.Length), span.Range);
        Assert.True(span.IsLocked);
        Assert.False(span.NeedsReview);
        Assert.Equal(LanguageDetectionSource.DefaultHint, span.Source);
        await repository.SaveAsync(_root, migrated);
        KaraokeProject reopened = await repository.LoadAsync(_root);
        Assert.Equal(span, Assert.Single(reopened.Lyrics.Lines[0].LanguageSpans!));
        Assert.Null(reopened.Lyrics.Lines[0].Presentation);
    }

    [Fact]
    public async Task UnknownPluginLanguageAndSavedPresentationRoundTripWithoutLoadingCode()
    {
        KaraokeProject project = CreateProject(LyricLanguage.Auto);
        LyricLine line = project.Lyrics.Lines[0];
        LyricUnit unit = line.Units[0] with { LanguageId = "x-missing-plugin" };
        var span = new LyricLanguageSpan(Guid.NewGuid(), new TextRange(0, line.PrimaryText.Length),
            "x-missing-plugin", LanguageDetectionSource.Manual, IsLocked: true,
            ManualReading: "saved reading", AdaptorVersion: "9.8.7");
        var snapshot = new LyricPresentationSnapshot(line.PrimaryText,
            [new LyricRecordingCell(unit.Id, "saved cell", "saved secondary", "saved romanization")],
            [new LyricSubtitleRun(new TextRange(0, line.PrimaryText.Length), "saved ruby", null, [unit.Id], span.LanguageId)],
            RomanizedSubtitleRuns: [new LyricSubtitleRun(new TextRange(0, line.PrimaryText.Length),
                "", "saved romanized ruby", [unit.Id], span.LanguageId)]);
        line = line with { Units = [unit], LanguageSpans = [span], Presentation = snapshot };
        project = project with { Lyrics = project.Lyrics with { Lines = [line] } };
        var repository = new JsonProjectRepository();
        await repository.SaveAsync(_root, project);

        KaraokeProject loaded = await repository.LoadAsync(_root);
        LyricLine reopened = Assert.Single(loaded.Lyrics.Lines);
        Assert.Equal("x-missing-plugin", Assert.Single(reopened.LanguageSpans!).LanguageId);
        Assert.Equal("x-missing-plugin", Assert.Single(reopened.Units).LanguageId);
        Assert.Equal(unit.Timing, reopened.Units[0].Timing);
        Assert.Equal(JsonSerializer.Serialize(snapshot, JsonOptions), JsonSerializer.Serialize(reopened.Presentation, JsonOptions));
    }

    [Fact]
    public void NewProjectsUseAutomaticLanguageSelection() =>
        Assert.Equal(LyricLanguage.Auto, KaraokeProject.Create("Auto", DateTimeOffset.UtcNow).Lyrics.Language);

    private static KaraokeProject CreateProject(LyricLanguage language)
    {
        var timing = new TimeRange(ProjectTime.FromSeconds(2), ProjectTime.FromSeconds(5));
        var unit = new LyricUnit(Guid.NewGuid(), [new TextRange(0, 1)], "はし", "hashi", timing,
            ProjectTime.FromSeconds(2.1), IsConfirmed: true);
        var line = new LyricLine(Guid.NewGuid(), "走", AnnotationType.Kana, "はし", "run", timing,
            [unit], ReviewState.Confirmed, ReadingSource.Manual, ReadingText: "はし", RomanizationText: "hashi");
        return KaraokeProject.Create("Migration", DateTimeOffset.UtcNow) with
        {
            Lyrics = new LyricDocument(language, [line]),
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
