using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Domain.Lyrics;

public enum LyricLanguage
{
    Chinese,
    Japanese,
    English,
    Auto,
    Unknown,
}

public enum AnnotationType
{
    None,
    Pinyin,
    Kana,
    Romaji,
    Translation,
}

public enum ReadingSource
{
    Generated,
    Confirmed,
    Manual,
}

public enum ReviewState
{
    Unreviewed,
    ReviewLater,
    Confirmed,
}

public readonly record struct TextRange
{
    [System.Text.Json.Serialization.JsonConstructor]
    public TextRange(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        Start = start;
        Length = length;
    }

    public int Start { get; }

    public int Length { get; }

    public int EndExclusive => checked(Start + Length);
}

public sealed record LyricUnit(
    Guid Id,
    IReadOnlyList<TextRange> DisplayRanges,
    string Reading,
    string? Romanization,
    TimeRange Timing,
    ProjectTime? RawInputTime,
    bool IsConfirmed,
    string? LanguageId = null);

public sealed record LyricLine(
    Guid Id,
    string PrimaryText,
    AnnotationType AnnotationType,
    string? AnnotationText,
    string? TranslationText,
    TimeRange Timing,
    IReadOnlyList<LyricUnit> Units,
    ReviewState ReviewState,
    ReadingSource? ReadingSource,
    bool IsInstrumental = false,
    bool IsHighlightEnabled = true,
    string? ReadingText = null,
    string? RomanizationText = null,
    IReadOnlyList<LyricLanguageSpan>? LanguageSpans = null,
    LyricPresentationSnapshot? Presentation = null);

public sealed record LyricDocument(
    LyricLanguage Language,
    IReadOnlyList<LyricLine> Lines,
    LyricSource? Source = null);

public sealed record LyricSource(string Provider, string Title, string Artist, string? SourceUrl,
    string ImportedFileRelativePath, DateTimeOffset ImportedAtUtc);
