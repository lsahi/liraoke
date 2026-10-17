using System.Globalization;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Languages;

/// <summary>Deterministic offline evidence routing. Script narrows candidates, never proves a language.</summary>
public sealed class MixedLanguageResolver(LanguageRegistry registry)
{
    public IReadOnlyList<LyricLanguageSpan> Resolve(string text, string? defaultHint = null,
        string? documentText = null, IReadOnlyList<LyricLanguageSpan>? lockedSpans = null)
    {
        if (text.Length == 0) { return []; }
        var locks = (lockedSpans ?? []).Where(span => span.IsLocked).OrderBy(span => span.Range.Start).ToArray();
        List<LyricLanguageSpan> result = [];
        string? documentLanguage = DominantLanguage(documentText ?? text);
        int cursor = 0;
        foreach (var locked in locks)
        {
            if (locked.Range.Start < cursor || locked.Range.EndExclusive > text.Length)
            { throw new InvalidOperationException("锁定的语言片段相互重叠或超出歌词范围。"); }
            Add(cursor, locked.Range.Start);
            result.Add(locked);
            cursor = locked.Range.EndExclusive;
        }
        Add(cursor, text.Length);
        return result;

        void Add(int start, int end)
        {
            if (end <= start) { return; }
            foreach (TextRange range in CandidateRanges(text, start, end, defaultHint, documentText ?? text))
            {
                string fragment = text[range.Start..range.EndExclusive];
                var context = new LanguageDetectionContext(fragment, text, documentText ?? text, defaultHint);
                var scores = registry.Adaptors.Select(adaptor => (Adaptor: adaptor, Evidence: Detect(adaptor, context)))
                    .OrderByDescending(item => item.Evidence.Score).ToArray();
                var best = scores.FirstOrDefault();
                if (best.Adaptor is not null && best.Evidence.Score >= 70
                    && (scores.Length < 2 || best.Evidence.Score - scores[1].Evidence.Score >= 15))
                {
                    result.Add(new(Guid.NewGuid(), range, best.Adaptor.LanguageId, LanguageDetectionSource.Automatic,
                        Diagnostic: best.Evidence.Reason, AdaptorVersion: best.Adaptor.Version));
                    continue;
                }
                string? inherited = null;
                var source = LanguageDetectionSource.Unknown;
                // Only ambiguous Han inherits a song's Japanese context. Latin remains independently assessed.
                if (defaultHint is not null && scores.Any(item => item.Adaptor.LanguageId == defaultHint && item.Evidence.Score > 0))
                { inherited = defaultHint; source = LanguageDetectionSource.DefaultHint; }
                else if (documentLanguage is not null && scores.Any(item => item.Adaptor.LanguageId == documentLanguage && item.Evidence.Score > 0))
                { inherited = documentLanguage; source = LanguageDetectionSource.Context; }
                result.Add(new(Guid.NewGuid(), range, inherited ?? LanguageIds.Unknown, source,
                    NeedsReview: source != LanguageDetectionSource.DefaultHint, Diagnostic: inherited is null ? "语言证据不足，请指定语言片段。" : "依据上下文或主要语言处理，请确认片段语言。",
                    AdaptorVersion: inherited is null ? null : registry.Find(inherited)?.Version,
                    DetectionNeedsReview: source != LanguageDetectionSource.DefaultHint));
            }
        }
    }

    private string? DominantLanguage(string documentText)
    {
        Dictionary<string, int> counts = [];
        foreach (string line in documentText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var context = new LanguageDetectionContext(line, line, documentText, null);
            var candidates = registry.Adaptors.Select(adaptor => (adaptor.LanguageId, Evidence: Detect(adaptor, context)))
                .OrderByDescending(item => item.Evidence.Score).ToArray();
            if (candidates.Length > 0 && candidates[0].Evidence.Score >= 70
                && (candidates.Length == 1 || candidates[0].Evidence.Score - candidates[1].Evidence.Score >= 15))
            { counts[candidates[0].LanguageId] = counts.GetValueOrDefault(candidates[0].LanguageId) + 1; }
        }
        var ranked = counts.OrderByDescending(pair => pair.Value).ToArray();
        return ranked.Length > 0 && (ranked.Length == 1 || ranked[0].Value > ranked[1].Value) ? ranked[0].Key : null;
    }

    private static LanguageEvidence Detect(ILanguageAdaptor adaptor, LanguageDetectionContext context)
    {
        try { return adaptor.Policy.Detect(context); }
        catch (Exception error) { throw new InvalidOperationException($"语言插件 {adaptor.DisplayName} 检测失败：{error.Message}", error); }
    }

    private IEnumerable<TextRange> CandidateRanges(string text, int start, int end, string? hint, string document)
    {
        var boundaries = StringInfo.ParseCombiningCharacters(text).Where(index => index >= start && index < end).Append(end).ToArray();
        int runStart = start, script = 0;
        var ranges = new List<TextRange>();
        for (int i = 0; i < boundaries.Length - 1; i++)
        {
            int next = ScriptFacts.Script(text[boundaries[i]..boundaries[i + 1]]);
            if (next != 0 && script != 0 && next != script)
            { ranges.Add(new(runStart, boundaries[i] - runStart)); runStart = boundaries[i]; }
            if (next != 0) { script = next; }
        }
        if (runStart < end) { ranges.Add(new(runStart, end - runStart)); }
        foreach (var range in ranges)
        {
            string fragment = text[range.Start..range.EndExclusive];
            var legal = StringInfo.ParseCombiningCharacters(fragment).Append(fragment.Length).ToHashSet();
            SortedSet<int> proposed = [0, fragment.Length];
            foreach (var adaptor in registry.Adaptors)
            {
                IReadOnlyList<int> suggestions;
                try { suggestions = adaptor.Policy.ProposeBoundaries(new(fragment, text, document, hint)); }
                catch (Exception error) { throw new InvalidOperationException($"语言插件 {adaptor.DisplayName} 分段失败：{error.Message}", error); }
                if (suggestions.Any(boundary => !legal.Contains(boundary)))
                { throw new InvalidOperationException($"语言插件 {adaptor.DisplayName} 返回了不完整字符边界。"); }
                proposed.UnionWith(suggestions);
            }
            int[] positions = proposed.ToArray();
            for (int i = 0; i + 1 < positions.Length; i++)
            { yield return new(range.Start + positions[i], positions[i + 1] - positions[i]); }
        }
    }
}
