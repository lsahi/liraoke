using System.Text.Json;
using System.Text.Json.Serialization;
using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Application.Preferences;

public sealed record SavedSubtitleStyle(Guid Id, string Name, SubtitleStyle Style);

public sealed record ApiConnectionPreference(string? Endpoint = null, string? Model = null, string? CredentialReference = null)
{
    // Future provider-specific non-secret settings must survive style edits.
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; init; }
}

public sealed record UserPreferences
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public IReadOnlyList<SavedSubtitleStyle> SubtitleStyles { get; init; } = [];
    // Credentials themselves belong to a future OS-protected store, never plaintext in this file.
    public IReadOnlyDictionary<string, ApiConnectionPreference> ApiConnections { get; init; } = new Dictionary<string, ApiConnectionPreference>();
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; init; }
}
