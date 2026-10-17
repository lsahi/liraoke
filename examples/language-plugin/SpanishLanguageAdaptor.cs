using System.Text.RegularExpressions;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace Liraoke.ExampleSpanish;

/// <summary>Small SDK demonstration only: not a linguistically complete Spanish analyser.</summary>
public sealed class SpanishLanguageAdaptor : BaseLanguageAdaptor
{
    public override string LanguageId => "es";
    public override string DisplayName => "Español（SDK 示例）";
    public override string Version => "0.1.0";
    public override BaseLanguagePolicy Policy { get; } = new SpanishLanguagePolicy();
}

public sealed class SpanishLanguagePolicy : BaseLanguagePolicy
{
    private static readonly Regex Words = new(@"\p{L}[\p{L}\p{M}]*(?:['’\-]\p{L}[\p{L}\p{M}]*)*",
        RegexOptions.CultureInvariant);
    private static readonly HashSet<string> EvidenceWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "hola", "corazón", "contigo", "siempre", "quiero", "noche", "mañana", "adiós",
    };

    public override LanguageEvidence Detect(LanguageDetectionContext context)
    {
        // A tiny, transparent dictionary is deliberate: shared Latin letters alone are NOT
        // evidence that text is Spanish. Unmatched input is left to other plugins/the user.
        int matches = Words.Matches(context.Text).Count(match => EvidenceWords.Contains(match.Value));
        return new LanguageEvidence(matches == 0 ? 0 : Math.Min(95, 70 + matches * 5),
            matches == 0 ? "示例词典没有明确证据" : $"匹配 {matches} 个示例西班牙语词语");
    }

    public override Task<LanguageAnalysisResult> AnalyzeAsync(LanguageAnalysisContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Match[] words = Words.Matches(context.Text).ToArray();
        List<LyricUnit> units = [];
        List<LyricRecordingCell> cells = [];
        List<LyricSubtitleRun> runs = [];
        long start = context.Timing.Start.Microseconds;
        long duration = context.Timing.Duration.Microseconds;
        for (int index = 0; index < words.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Match word = words[index];
            Guid id = Guid.NewGuid();
            var range = new TextRange(word.Index, word.Length);
            var timing = new TimeRange(new ProjectTime(start + duration * index / words.Length),
                new ProjectTime(start + duration * (index + 1) / words.Length));
            units.Add(new LyricUnit(id, [range], word.Value, null, timing, null,
                IsConfirmed: false, LanguageId: "es"));
            cells.Add(new LyricRecordingCell(id, word.Value));
            runs.Add(new LyricSubtitleRun(range, "", null, [id], "es"));
        }
        return Task.FromResult(new LanguageAnalysisResult(units, cells, runs,
            Diagnostic: "SDK 示例按单词分组；尚未实现西班牙语演唱音节或读音校对。"));
    }
}
