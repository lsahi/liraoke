using System.Text.Json;
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
