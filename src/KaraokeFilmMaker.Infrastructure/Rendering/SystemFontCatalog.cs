using System.Windows.Media;

namespace KaraokeFilmMaker.Infrastructure.Rendering;

/// <summary>One local font inventory shared by editing, preview and export. Never downloads fonts.</summary>
public static class SystemFontCatalog
{
    private static readonly Lazy<FontInventory> Inventory = new(CreateInventory);
    public static IReadOnlyList<string> Names => Inventory.Value.Names;
    public static bool IsInstalled(string name) => Inventory.Value.Aliases.ContainsKey(name.Trim());
    public static string Resolve(string name) => Inventory.Value.Aliases.TryGetValue(name.Trim(), out string? family)
        ? family : Inventory.Value.Fallback;

    public static IReadOnlyList<string> Search(string text)
    {
        string query = text.Trim();
        if (query.Length == 0) { return Names; }
        var matches = Inventory.Value.Aliases.Where(alias => alias.Key.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Select(alias => alias.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Names.Where(matches.Contains).ToArray();
    }

    private static FontInventory CreateInventory()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (FontFamily family in Fonts.SystemFontFamilies)
        {
            aliases.TryAdd(family.Source, family.Source);
            foreach (string name in family.FamilyNames.Values) { aliases.TryAdd(name, family.Source); }
        }
        string[] names = aliases.Values.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        string fallback = aliases.ContainsKey("Yu Gothic UI") ? aliases["Yu Gothic UI"]
            : aliases.ContainsKey("Segoe UI") ? aliases["Segoe UI"] : names.FirstOrDefault() ?? "Global User Interface";
        return new(names, aliases, fallback);
    }

    private sealed record FontInventory(IReadOnlyList<string> Names, IReadOnlyDictionary<string, string> Aliases, string Fallback);
}
