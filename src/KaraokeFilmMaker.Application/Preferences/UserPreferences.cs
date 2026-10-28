using System.Text.Json;
using System.Text.Json.Serialization;
using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Application.Preferences;

public sealed record SavedSubtitleStyle(Guid Id, string Name, SubtitleStyle Style);

// Only the encrypted credential payload is persisted; secrets are never project data.
public sealed record AiProviderPreference(Guid Id, [property: JsonRequired] string ProviderName, [property: JsonRequired] bool IsOfficial,
    string? ApiPath = null, string? ProtectedCredentials = null);

public sealed record AiCredentials(string? APIKey = null, string? AK = null, string? SK = null);

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
    public IReadOnlyList<SingingRole> Roles { get; init; } = [];
    public IReadOnlyList<AiProviderPreference> AiProviders { get; init; } = [];
    // Legacy connection metadata is retained. AiProviders holds only OS-protected credential payloads.
    public IReadOnlyDictionary<string, ApiConnectionPreference> ApiConnections { get; init; } = new Dictionary<string, ApiConnectionPreference>();
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; init; }
}
