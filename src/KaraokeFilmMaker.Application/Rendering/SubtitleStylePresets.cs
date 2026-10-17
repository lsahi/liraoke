using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Application.Rendering;

public sealed record SubtitleStylePreset(string Id, string Name, string InactiveColor, string ActiveColor,
    string OutlineColor, string ActiveOutlineColor, double OutlineWidth, double ShadowOffset)
{
    // A visual preset must not replace the user's installed font, layout or ruby preference.
    public SubtitleStyle ApplyTo(SubtitleStyle current) => current with
    {
        InactiveColor = InactiveColor, ActiveColor = ActiveColor, OutlineColor = OutlineColor,
        ActiveOutlineColor = ActiveOutlineColor, OutlineWidth = OutlineWidth, ShadowOffset = ShadowOffset,
    };
}

public static class SubtitleStylePresets
{
    public static IReadOnlyList<SubtitleStylePreset> All { get; } = Array.AsReadOnly<SubtitleStylePreset>(
    [
        new("liraoke", "Liraoke · 白蓝 / 黄白", "#FFFFFFFF", "#FFFFD54F", "#FF24465F", "#FFFFFFFF", 3, 2),
        new("classic", "经典卡拉 OK · 白黑 / 黄黑", "#FFFFFFFF", "#FFFFD54F", "#FF000000", "#FF000000", 3, 2),
        new("sky", "晴空 · 白蓝 / 浅蓝", "#FFFFFFFF", "#FF83CDE8", "#FF24465F", "#FF24465F", 3, 1),
        new("minimal", "简约 · 白灰 / 淡黄", "#FFFFFFFF", "#FFFFE59A", "#FF34424D", "#FF34424D", 1.5, 1),
    ]);
}
