using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Languages;

public sealed record LanguageCapabilities(bool HasAutomaticReadings = false,
    bool HasRomanization = false, LyricTextDirection TextDirection = LyricTextDirection.LeftToRight,
    bool SupportsManualReading = false);

public sealed record LanguageDetectionContext(string Text, string FullLine,
    string DocumentText, string? DefaultHint);

/// <summary>Comparable evidence, not a statistical probability. Zero means no positive evidence.</summary>
public sealed record LanguageEvidence(int Score, string Reason);

public sealed record LanguageAnalysisContext(string Text, TimeRange Timing, string? ManualReading = null);
public sealed record LanguageAnalysisResult(IReadOnlyList<LyricUnit> Units,
    IReadOnlyList<LyricRecordingCell> RecordingCells, IReadOnlyList<LyricSubtitleRun> SubtitleRuns,
    bool NeedsReview = false, string? Diagnostic = null, string? ReadingText = null,
    string? RomanizationText = null,
    IReadOnlyList<LyricSubtitleRun>? RomanizedSubtitleRuns = null);

/// <summary>A complete language package. Implementations are invoked only during explicit analysis.</summary>
public interface ILanguageAdaptor
{
    int ApiVersion { get; }
    string LanguageId { get; }
    string DisplayName { get; }
    string Version { get; }
    LanguageCapabilities Capabilities { get; }
    BaseLanguagePolicy Policy { get; }
}

public abstract class BaseLanguageAdaptor : ILanguageAdaptor
{
    public const int CurrentApiVersion = 1;
    public virtual int ApiVersion => CurrentApiVersion;
    public abstract string LanguageId { get; }
    public abstract string DisplayName { get; }
    public virtual string Version => "1.0.0";
    public virtual LanguageCapabilities Capabilities => new();
    public abstract BaseLanguagePolicy Policy { get; }
}

/// <summary>Policies return local UTF-16 ranges and immutable data, never WPF controls.</summary>
public abstract class BaseLanguagePolicy
{
    public abstract LanguageEvidence Detect(LanguageDetectionContext context);
    /// <summary>Optional local grapheme boundaries, e.g. a distinctive foreign phrase inside one script.</summary>
    public virtual IReadOnlyList<int> ProposeBoundaries(LanguageDetectionContext context) => [];
    public abstract Task<LanguageAnalysisResult> AnalyzeAsync(LanguageAnalysisContext context,
        CancellationToken cancellationToken = default);
    public virtual string? GetRecordingIssue(LyricLine line, LyricLanguageSpan span) => null;
    public virtual LyricPresentationSnapshot CreatePresentation(LyricLine line) =>
        LanguagePresentation.BuildPlain(line);
}
