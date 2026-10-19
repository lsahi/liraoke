using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Timeline;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Domain.Projects;

public sealed record SubtitleStyle(
    string FontFamily,
    double PrimaryFontSize,
    double AnnotationFontSize,
    string InactiveColor,
    string ActiveColor,
    string OutlineColor,
    double OutlineWidth,
    double ShadowOffset,
    double SafeAreaRatio = 0.30,
    double LineGap = 8,
    double MarginRatio = 0.04,
    bool ShowRomanization = false)
{
    // Missing in older projects: preserve their single-outline appearance.
    public string? ActiveOutlineColor { get; init; }
}

public sealed record ExportPreset(
    int Width,
    int Height,
    int FramesPerSecond,
    int VideoBitrateKbps,
    int AudioBitrateKbps);

public sealed record KaraokeProject(
    int SchemaVersion,
    Guid Id,
    string Title,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ModifiedAtUtc,
    Guid? MasterAudioAssetId,
    IReadOnlyList<MediaAsset> MediaAssets,
    LyricDocument Lyrics,
    IReadOnlyList<VideoClip> VideoClips,
    SubtitleStyle SubtitleStyle,
    ExportPreset ExportPreset,
    int InputOffsetMilliseconds = 0,
    int VideoTrackCount = 1,
    MetronomeSettings? Metronome = null)
{
    public const int CurrentSchemaVersion = 9;
    public const int MaximumVideoTracks = 1;

    public bool AudioVisualizationEnabled { get; init; }
    // Only successfully generated full-song files are registered. They are not library assets.
    public IReadOnlyList<Accompaniment> Accompaniments { get; init; } = [];

    public Accompaniment? GetAccompaniment(Guid? sourceId) => Accompaniments.FirstOrDefault(item =>
        item.SourceAssetId == sourceId && MediaAssets.Any(source => source.Id == sourceId
            && source.ContentHash == item.SourceContentHash && source.Duration == item.Duration));
    public AudioVisualizationStyle AudioVisualizationStyle { get; init; } = AudioVisualizationStyle.Classic;

    // Non-destructive output range in original project time. Null means the full song.
    public TimeRange? WorkArea { get; init; }

    public TimeRange GetWorkArea() => WorkArea ?? new TimeRange(ProjectTime.Zero,
        ProjectTime.Zero + (MediaAssets.FirstOrDefault(asset => asset.Id == MasterAudioAssetId)?.Duration ?? ProjectDuration.Zero));

    public static KaraokeProject Create(string title, DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        return new KaraokeProject(
            CurrentSchemaVersion,
            Guid.NewGuid(),
            title.Trim(),
            createdAtUtc,
            createdAtUtc,
            MasterAudioAssetId: null,
            MediaAssets: [],
            Lyrics: new LyricDocument(LyricLanguage.Auto, []),
            VideoClips: [],
            SubtitleStyle: new SubtitleStyle(
                "Yu Gothic UI",
                PrimaryFontSize: 64,
                AnnotationFontSize: 28,
                InactiveColor: "#FFFFFFFF",
                ActiveColor: "#FFFFD54F",
                OutlineColor: "#FF24465F",
                OutlineWidth: 3,
                ShadowOffset: 2) { ActiveOutlineColor = "#FFFFFFFF" },
            ExportPreset: new ExportPreset(
                Width: 1920,
                Height: 1080,
                FramesPerSecond: 30,
                VideoBitrateKbps: 12_000,
                AudioBitrateKbps: 320),
            Metronome: new MetronomeSettings());
    }
}
