namespace KaraokeFilmMaker.Domain.Projects;

public sealed record SingingRole(Guid Id, string Name, SubtitleStyle Style);

public enum SongTitlePosition { TopLeft, TopRight }

public sealed record SongTitleSettings
{
    public bool Enabled { get; init; }
    // Null follows the original master filename; an explicit string is user-owned.
    public string? Text { get; init; }
    public SongTitlePosition Position { get; init; }
    public string FontFamily { get; init; } = "Yu Gothic UI";
    public double FontSize { get; init; } = 48;
    public string Color { get; init; } = "#FFFFFFFF";
    public string OutlineColor { get; init; } = "#FF24465F";
    public double OutlineWidth { get; init; } = 2;
    public double ShadowOffset { get; init; } = 2;
    public double MarginRatio { get; init; } = .04;

    public string ResolveText(KaraokeProject project) => Text ??
        Path.GetFileNameWithoutExtension(project.MediaAssets.FirstOrDefault(item => item.Id == project.MasterAudioAssetId)?.DisplayName ?? "");
}
