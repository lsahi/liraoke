namespace KaraokeFilmMaker.Domain.Lyrics;

public static class LanguageIds
{
    public const string Chinese = "zh";
    public const string English = "en";
    public const string Japanese = "ja";
    public const string Unknown = "und";

    public static string? FromLegacy(LyricLanguage language) => language switch
    {
        LyricLanguage.Chinese => Chinese, LyricLanguage.English => English,
        LyricLanguage.Japanese => Japanese, _ => null,
    };
    public static LyricLanguage ToLegacy(string? id) => id switch
    {
        Chinese => LyricLanguage.Chinese, English => LyricLanguage.English,
        Japanese => LyricLanguage.Japanese, _ => LyricLanguage.Unknown,
    };
}

public enum LanguageDetectionSource { Automatic, Context, DefaultHint, Manual, Unknown }
public enum LyricTextDirection { LeftToRight, RightToLeft }

/// <summary>UTF-16 source range; boundaries must coincide with complete graphemes.</summary>
public sealed record LyricLanguageSpan(Guid Id, TextRange Range, string LanguageId,
    LanguageDetectionSource Source, bool NeedsReview = false, bool IsLocked = false,
    string? ManualReading = null, string? Diagnostic = null, string? AdaptorVersion = null,
    bool DetectionNeedsReview = false)
{
    /// <summary>Cached analysis/policy error, distinct from detection confidence. Null means legacy metadata.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasBlockingIssue { get; init; }
}

/// <summary>Persisted, host-renderable data. No plugin is required to replay this snapshot.</summary>
public sealed record LyricPresentationSnapshot(string SourceText,
    IReadOnlyList<LyricRecordingCell> RecordingCells,
    IReadOnlyList<LyricSubtitleRun> SubtitleRuns,
    int FormatVersion = 1,
    IReadOnlyList<LyricSubtitleRun>? RomanizedSubtitleRuns = null);

public sealed record LyricRecordingCell(Guid UnitId, string PrimaryText,
    string? SecondaryText = null, string? Romanization = null);

/// <summary>Subtitle ruby and singing units deliberately have independent boundaries.</summary>
public sealed record LyricSubtitleRun(TextRange Range, string Annotation,
    string? Romanization, IReadOnlyList<Guid> UnitIds, string? LanguageId = null);
