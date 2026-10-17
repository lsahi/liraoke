using System.Text.Json;
using System.Text.Json.Serialization;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Validation;
using KaraokeFilmMaker.Infrastructure.Workspaces;

namespace KaraokeFilmMaker.Infrastructure.Persistence;

public sealed class JsonProjectRepository(string fileName = "project.kfp") : IProjectRepository
{
    private readonly string _fileName = fileName is "project.kfp" or "project.recovery.kfp"
        ? fileName : throw new ArgumentException("Unsupported project file name.", nameof(fileName));

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<KaraokeProject> LoadAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        string projectPath = GetProjectPath(workspacePath);
        await using FileStream stream = File.OpenRead(projectPath);
        var project = await JsonSerializer.DeserializeAsync<KaraokeProject>(
            stream,
            SerializerOptions,
            cancellationToken);

        if (project is null)
        {
            throw new InvalidDataException("The project file is empty or invalid.");
        }

        project = MigrateLegacyProject(project);
        IReadOnlyList<string> errors = ProjectValidator.Validate(project);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        return project;
    }

    public async Task SaveAsync(
        string workspacePath,
        KaraokeProject project,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> errors = ProjectValidator.Validate(project);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        string projectPath = GetProjectPath(workspacePath);
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        string temporaryPath = projectPath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, project, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, projectPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetProjectPath(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        return _fileName == "project.recovery.kfp"
            ? Path.Combine(Path.GetFullPath(workspacePath), _fileName)
            : WorkspaceProjectPath.Resolve(workspacePath);
    }

    private static KaraokeProject MigrateLegacyProject(KaraokeProject project)
    {
        if (project.SchemaVersion <= 0 || project.SchemaVersion >= KaraokeProject.CurrentSchemaVersion) { return project; }
        project = MigrateLegacyLanguageMetadata(project);
        // Empty historical tracks are harmless. Actual extra-track content is rejected by validation.
        if (project.VideoClips.All(clip => clip.TrackIndex == 0)) { project = project with { VideoTrackCount = 1 }; }
        return project with { SchemaVersion = KaraokeProject.CurrentSchemaVersion };
    }

    private static KaraokeProject MigrateLegacyLanguageMetadata(KaraokeProject project)
    {
        if (project.SchemaVersion >= 3) { return project; }

        // Migration is metadata-only: never re-analyze text, timings or manually corrected readings.
        // Missing presentation snapshots intentionally retain the legacy renderer's behavior.
        string languageId = LanguageIds.FromLegacy(project.Lyrics.Language) ?? LanguageIds.Unknown;
        var lines = project.Lyrics.Lines.Select(line =>
            line.PrimaryText.Length == 0 || line.LanguageSpans is { Count: > 0 }
                ? line
                : line with
                {
                    LanguageSpans = [new LyricLanguageSpan(line.Id,
                        new TextRange(0, line.PrimaryText.Length), languageId,
                        LanguageDetectionSource.DefaultHint, IsLocked: true)],
                }).ToArray();
        return project with
        {
            SchemaVersion = KaraokeProject.CurrentSchemaVersion,
            Lyrics = project.Lyrics with { Lines = lines },
        };
    }
}
