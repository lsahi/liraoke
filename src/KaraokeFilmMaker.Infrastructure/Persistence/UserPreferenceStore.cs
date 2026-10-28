using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using KaraokeFilmMaker.Application.Preferences;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Validation;

namespace KaraokeFilmMaker.Infrastructure.Persistence;

/// <summary>App-wide portable preferences, separate from projects, recovery and generated media.</summary>
public sealed class UserPreferenceStore(string appDirectory)
{
    public string FilePath { get; } = Path.Combine(Path.GetFullPath(appDirectory), "user-preferences.json");
    private static readonly JsonSerializerOptions Options = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public UserPreferences Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            if (new FileInfo(FilePath).Length > 4 * 1024 * 1024) throw new InvalidDataException("偏好文件过大。");
            var preferences = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(FilePath), Options)
                ?? throw new InvalidDataException("偏好文件为空。");
            Validate(preferences);
            return preferences;
        }
        catch (JsonException e) { throw new InvalidDataException("用户偏好文件损坏，未覆盖原文件。可检查同目录的 .bak 备份。", e); }
    }

    public UserPreferences SaveStyle(string name, SubtitleStyle style, bool overwrite = false)
    {
        name = name.Trim();
        return Update(current =>
        {
            var previous = current.SubtitleStyles.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (previous is not null && !overwrite) throw new InvalidOperationException("已有同名样式，请确认覆盖或使用其他名称。");
            var saved = new SavedSubtitleStyle(previous?.Id ?? Guid.NewGuid(), name, style);
            return current with { SubtitleStyles = [.. current.SubtitleStyles.Where(item => item.Id != saved.Id), saved] };
        });
    }

    public UserPreferences DeleteStyle(Guid id) => Update(current =>
        current with { SubtitleStyles = current.SubtitleStyles.Where(item => item.Id != id).ToArray() });

    public UserPreferences SaveRole(string name, SubtitleStyle style, bool overwrite = false) => Update(current =>
    {
        name = name.Trim();
        var previous = current.Roles.FirstOrDefault(role => string.Equals(role.Name, name, StringComparison.OrdinalIgnoreCase));
        if (previous is not null && !overwrite) throw new InvalidOperationException("已有同名角色，请确认覆盖或使用其他名称。");
        var role = new SingingRole(previous?.Id ?? Guid.NewGuid(), name, style);
        return current with { Roles = [.. current.Roles.Where(item => item.Id != role.Id), role] };
    });

    public UserPreferences DeleteRole(Guid id) => Update(current =>
        current with { Roles = current.Roles.Where(role => role.Id != id).ToArray() });

    public UserPreferences SaveAiProvider(Guid id, string providerName, bool? isOfficial,
        string? apiPath, AiCredentials credentials)
    {
        if (isOfficial is null) throw new InvalidDataException("请选择官方 API 或中转站 API（isOfficial）。");
        string name = providerName.Trim();
        string? path = string.IsNullOrWhiteSpace(apiPath) ? null : apiPath.Trim();
        var provider = new AiProviderPreference(id, name, isOfficial.Value, path);
        ValidateAiProviders([provider]);
        var normalized = new AiCredentials(EmptyToNull(credentials.APIKey), EmptyToNull(credentials.AK), EmptyToNull(credentials.SK));
        if (new[] { normalized.APIKey, normalized.AK, normalized.SK }.Any(value => value?.Length > 16384))
            throw new InvalidDataException("凭据内容过长，未保存。");
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(normalized);
        try
        {
            string encrypted = Convert.ToBase64String(ProtectedData.Protect(plaintext, CredentialEntropy, DataProtectionScope.CurrentUser));
            return Update(current => current with
            {
                AiProviders = [.. current.AiProviders.Where(item => item.Id != id), provider with { ProtectedCredentials = encrypted }],
            });
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public UserPreferences DeleteAiProvider(Guid id) => Update(current => current with
        { AiProviders = current.AiProviders.Where(item => item.Id != id).ToArray() });

    public static AiCredentials ReadAiCredentials(AiProviderPreference provider)
    {
        if (provider.ProtectedCredentials is null) return new();
        try
        {
            byte[] plaintext = ProtectedData.Unprotect(Convert.FromBase64String(provider.ProtectedCredentials),
                CredentialEntropy, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<AiCredentials>(plaintext) ?? throw new JsonException(); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception e) when (e is CryptographicException or FormatException or JsonException)
        { throw new InvalidDataException("无法解密此 AI 配置的凭据。可能来自另一台电脑或 Windows 用户，请重新输入凭据后保存。"); }
    }

    private static readonly byte[] CredentialEntropy = Encoding.UTF8.GetBytes("Liraoke.AiCredentials.v1");
    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static void ValidateAiProviders(IReadOnlyList<AiProviderPreference>? providers)
    {
        if (providers is null || providers.Count > 200) throw new InvalidDataException("AI 配置结构无效，最多保存 200 项。");
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in providers)
        {
            if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id) || string.IsNullOrWhiteSpace(item.ProviderName)
                || item.ProviderName != item.ProviderName.Trim() || item.ProviderName.Length > 80
                || item.ProviderName.Any(char.IsControl) || !names.Add(item.ProviderName))
                throw new InvalidDataException("providerName 必填，限 1–80 个字符，配置名称不能重复。");
            if (item.ApiPath is not null && (!Uri.TryCreate(item.ApiPath, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)))
                throw new InvalidDataException("apiPath 请填写完整的 HTTP / HTTPS 地址，或留空；不要在地址中嵌入账户密码。");
            if (item.ProtectedCredentials?.Length > 100000) throw new InvalidDataException("AI 凭据数据无效。");
        }
    }

    private UserPreferences Update(Func<UserPreferences, UserPreferences> change)
    {
        // No silent fallback to another directory; user preferences really live alongside the EXE.
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using var guard = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var updated = change(Load()); // Read-modify-write under the lock, preserving other instances' settings.
        Validate(updated);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, updated, Options);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
            return updated;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(UserPreferences preferences)
    {
        ValidateAiProviders(preferences.AiProviders);
        if (preferences.SchemaVersion != UserPreferences.CurrentSchemaVersion)
            throw new InvalidDataException("此用户偏好格式不受当前版本支持，未改写文件。");
        if (preferences.SubtitleStyles is null || preferences.ApiConnections is null || preferences.SubtitleStyles.Count > 200
            || preferences.Roles is null || preferences.Roles.Count > 200)
            throw new InvalidDataException("用户偏好结构无效，或自定义样式超过 200 个。");
        foreach (var collection in new[] { preferences.SubtitleStyles, preferences.Roles.Select(role =>
            role is null ? null! : new SavedSubtitleStyle(role.Id, role.Name, role.Style)).ToArray() })
        {
          var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
          var ids = new HashSet<Guid>();
          foreach (var item in collection)
          {
            if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id) || string.IsNullOrWhiteSpace(item.Name)
                || item.Name != item.Name.Trim() || item.Name.Length > 80 || item.Name.Any(char.IsControl) || !names.Add(item.Name))
                throw new InvalidDataException("样式名称须为 1–80 个字符，且名称和标识不能重复。");
            if (!ProjectValidator.IsValidStyle(item.Style))
                throw new InvalidDataException("自定义样式参数无效，未改写偏好文件。");
          }
        }
    }
}
