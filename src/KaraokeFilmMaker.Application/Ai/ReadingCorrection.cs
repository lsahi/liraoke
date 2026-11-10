using System.Text;
using System.Text.Json;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Application.Japanese;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Ai;

public sealed record AsrPreference(bool Local = false, string Endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
    string Model = "qwen3-asr-flash", string? ProtectedCredentials = null, Guid? CorrectionProviderId = null,
    string Protocol = "qwen-chat");
public sealed record HeardAudio(double StartSeconds, double EndSeconds, string Text, bool Failed = false);
public sealed record ReadingSegment(string Surface, string Reading, string Language = "ja", bool Uncertain = true,
    string Reason = "", string Evidence = "", bool NotSung = false);
public sealed record ReadingProposal(Guid LineId, string OriginalText, string Text, IReadOnlyList<ReadingSegment> Segments);
public sealed record ReadingUpdate(Guid LineId, ReadingProposal? Proposal, string? Error = null,
    IReadOnlyList<HeardAudio>? Evidence = null);
public interface IReadingCorrectionService
{
    IAsyncEnumerable<ReadingUpdate> CorrectAsync(string audioPath, double duration, LyricDocument lyrics,
        AsrPreference settings, Preferences.AiCredentials credentials, Preferences.AiProviderPreference mapper,
        Preferences.AiCredentials mapperCredentials, IProgress<string> progress, CancellationToken token);
}

public sealed class ReadingMappingException(string code, string message) : InvalidOperationException(message)
{ public string Code { get; } = code; }

public static class ReadingCorrection
{
    public static bool IsJapanese(LyricLine line, LyricLanguage language) => !line.IsInstrumental &&
        (line.LanguageSpans?.Any(s => s.LanguageId == LanguageIds.Japanese) == true ||
         line.Units.Any(u => u.LanguageId == LanguageIds.Japanese) ||
         language == LyricLanguage.Japanese || line.PrimaryText.Any(c => c is >= 'ぁ' and <= 'ヿ'));

    public static bool IsProtected(LyricLine line) => line.ReadingSource is ReadingSource.Manual or ReadingSource.Confirmed
        || line.Units.Any(u => u.IsConfirmed || u.RawInputTime is not null || u.RoleIds is { Count: > 0 })
        || line.LanguageSpans?.Any(s => s.IsLocked && s.Source == LanguageDetectionSource.Manual) == true
        || line.ExplicitEndTime is not null;

    public static string NormalizeKana(string value) => new(value.Normalize(NormalizationForm.FormKC)
        .Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c))
        .Select(c => c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c).ToArray());

    public static void Validate(ReadingProposal proposal, LyricLine source)
    {
        if (proposal.LineId != source.Id || proposal.OriginalText != source.PrimaryText)
            throw new ReadingMappingException("stale-source", "返回的目标行不匹配。");
        if (string.IsNullOrWhiteSpace(proposal.Text) || proposal.Text.Length > 4000 || proposal.Text.Any(char.IsControl)
            || proposal.Segments is null || proposal.Segments.Count is 0 or > 500
            || proposal.Segments.Any(s => s is null || string.IsNullOrEmpty(s.Surface) || s.Reading is null || s.Reason is null || s.Evidence is null
                || s.Reading.Length > 2000 || s.Reason.Length > 500 || s.Evidence.Length > 4000 || s.Language is not ("ja" or "en" or "zh")))
            throw new ReadingMappingException("invalid-fields", "分段字段缺失或类型、长度不正确。");
        if (string.Concat(proposal.Segments.Select(s => s.Surface)) != proposal.Text)
            throw new ReadingMappingException("surface-coverage", "分段文字未完整覆盖目标歌词（可能遗漏括号或重复文字）。");
        if (proposal.Segments.Any(s => s.NotSung && (s.Reading.Length != 0 || !IsParenthetical(s.Surface))))
            throw new ReadingMappingException("annotation-shape", "不演唱片段必须是完整括号注音且没有重复读音。");
        foreach (var s in proposal.Segments.Where(s => !s.NotSung && s.Language == "ja" && s.Surface.Any(char.IsLetterOrDigit)))
            if (string.IsNullOrWhiteSpace(s.Reading) || s.Reading.Any(c => !(c is >= 'ぁ' and <= 'ヿ') && !char.IsWhiteSpace(c) && !char.IsPunctuation(c)))
                throw new ReadingMappingException("missing-kana", "演唱片段缺少假名读音或仍含汉字／罗马字。");
    }

    private static bool IsParenthetical(string text) => text.Length >= 3 &&
        ((text[0] == '(' && text[^1] == ')') || (text[0] == '（' && text[^1] == '）')) &&
        !text[1..^1].Any(c => c is '(' or ')' or '（' or '）');

    // Normalize only explicitly classified annotations, never parentheses merely because they contain kana.
    public static ReadingProposal NormalizeAnnotations(ReadingProposal proposal, IEnumerable<string>? lyricContext = null)
    {
        if (proposal.Segments is null || proposal.Segments.Any(s => s is null || string.IsNullOrEmpty(s.Surface) || s.Reading is null)) return proposal;
        var alternatives = lyricContext?.ToHashSet(StringComparer.Ordinal);
        List<ReadingSegment> expanded = [];
        foreach (var segment in proposal.Segments) {
            int open = segment.Surface.IndexOfAny(['(', '（']);
            if (segment.NotSung && open > 0 && segment.Surface[..open].All(IsKanji)
                && IsParenthetical(segment.Surface[open..]) && IsKanaAnnotation(segment.Surface[(open + 1)..^1])) {
                expanded.Add(segment with { Surface = segment.Surface[..open], Reading = segment.Surface[(open + 1)..^1], NotSung = false });
                expanded.Add(segment with { Surface = segment.Surface[open..], Reading = "" });
            } else expanded.Add(segment);
        }
        List<ReadingSegment> normalized = [];
        for (int i = 0; i < expanded.Count; i++) {
            var first = expanded[i];
            bool consumed = false;
            if (first.Surface[0] is '(' or '（') {
                string block = ""; bool annotation = false;
                for (int j = i; j < expanded.Count; j++) {
                    block += expanded[j].Surface; annotation |= expanded[j].NotSung && expanded[j].Surface.Any(char.IsLetterOrDigit);
                    if (IsParenthetical(block)) {
                        // A repeated lyric without this block corroborates a pronunciation gloss.
                        // Require the reading of the immediately preceding kanji span, not just any kana suffix.
                        int offset = normalized.Sum(s => s.Surface.Length);
                        string priorReading = "";
                        for (int k = normalized.Count - 1; k >= 0 && normalized[k].Surface.All(IsKanji); k--)
                            priorReading = normalized[k].Reading + priorReading;
                        string inside = block[1..^1];
                        bool corroborated = alternatives is not null && IsKanaAnnotation(inside)
                            && NormalizeKana(priorReading) == NormalizeKana(inside)
                            && offset + block.Length <= proposal.Text.Length
                            && alternatives.Contains(proposal.Text.Remove(offset, block.Length));
                        annotation |= corroborated;
                        if (annotation) {
                            string kana = block[1..^1];
                            // Honor an explicit pronunciation annotation even when the model left the dictionary reading on its word.
                            if (normalized.Count > 0 && normalized[^1].Surface.All(IsKanji) && IsKanaAnnotation(kana)
                                && !NormalizeKana(string.Concat(normalized.Select(x => x.Reading))).EndsWith(NormalizeKana(kana), StringComparison.Ordinal))
                                normalized[^1] = normalized[^1] with { Reading = kana };
                            normalized.Add(first with { Surface = block, Reading = "", NotSung = true });
                            i = j; consumed = true;
                        }
                        break;
                    }
                    if (block.Skip(1).Any(c => c is '(' or ')' or '（' or '）')) break;
                }
            }
            if (!consumed) normalized.Add(first.NotSung && !first.Surface.Any(char.IsLetterOrDigit)
                ? first with { NotSung = false, Reading = "" } : first);
        }
        return proposal with { Segments = normalized };
    }
    private static bool IsKanji(char c) => c is >= '\u3400' and <= '\u9fff' or '々';
    private static bool IsKanaAnnotation(string text) => text.Any(c => c is >= 'ぁ' and <= 'ヿ')
        && text.All(c => c is >= 'ぁ' and <= 'ヿ' || char.IsWhiteSpace(c));

    public static async Task<LyricLine> ApplyAsync(LyricLine source, ReadingProposal proposal,
        LanguageAnalysisService analysis, IReadOnlyList<HeardAudio>? evidence = null, CancellationToken token = default)
    {
        proposal = NormalizeAnnotations(proposal);
        Validate(proposal, source);
        if (IsProtected(source)) return Failed(source, "已保留人工读音、角色单元和已录时间；AI 建议可在校正详情中查看。", proposal);
        // Remove annotation text before constructing ranges so neither timing nor subtitle rendering can display it.
        var sung = proposal.Segments.Where(s => !s.NotSung).ToArray();
        proposal = proposal with { Text = string.Concat(sung.Select(s => s.Surface)), Segments = sung };
        Validate(proposal, source);
        List<LyricLanguageSpan> spans = [];
        int pos = 0;
        foreach (var s in proposal.Segments)
        {
            var range = new TextRange(pos, s.Surface.Length); pos += s.Surface.Length;
            spans.Add(new(Guid.NewGuid(), range, s.Language, LanguageDetectionSource.Automatic,
                IsLocked: true, ManualReading: s.Language == "ja" ? (s.NotSung ? "" : s.Reading) : null));
        }
        // Group adjacent language spans for singing; keep token boundaries separately for ruby mapping.
        List<LyricLanguageSpan> groups = [];
        foreach (var span in spans)
        {
            if (groups.Count > 0 && groups[^1].LanguageId == span.LanguageId)
                groups[^1] = groups[^1] with { Range = new(groups[^1].Range.Start, groups[^1].Range.Length + span.Range.Length),
                    ManualReading = groups[^1].ManualReading + span.ManualReading };
            else groups.Add(span);
        }
        var rebuilt = await analysis.AnalyzeLineAsync(source with { PrimaryText = proposal.Text, Units = [],
            LanguageSpans = groups, Presentation = null, ReadingSource = null, ReadingText = null, AnnotationText = null,
            RomanizationText = null, ExplicitEndTime = null }, force: true, cancellationToken: token);
        var units = rebuilt.Units.ToList();
        foreach (var group in groups.Where(g => g.LanguageId == LanguageIds.Japanese))
        {
            List<JapaneseToken> tokens = []; int offset = 0;
            foreach (var s in proposal.Segments)
            {
                if (offset >= group.Range.Start && offset < group.Range.EndExclusive && !s.NotSung) {
                    string reading = new(s.Reading.Where(c => !char.IsPunctuation(c) && !char.IsWhiteSpace(c)).ToArray());
                    if (reading.Length > 0) tokens.Add(new(s.Surface, reading, KanaRomanizer.Romanize(reading), offset, s.Surface.Length, true));
                }
                offset += s.Surface.Length;
            }
            var old = units.Where(u => u.DisplayRanges.All(r => r.Start >= group.Range.Start && r.EndExclusive <= group.Range.EndExclusive)).ToArray();
            var timing = old.Length == 0 ? source.Timing : new TimeRange(old[0].Timing.Start, old[^1].Timing.End);
            units.RemoveAll(u => old.Contains(u));
            units.AddRange(JapaneseAnnotationService.BuildUnits(tokens, timing));
        }
        rebuilt = analysis.RefreshPresentation(rebuilt with { Units = units.OrderBy(u => u.DisplayRanges.Min(r => r.Start)).ThenBy(u => u.Timing.Start).ToArray() });
        return rebuilt with { ReadingSource = ReadingSource.Generated, ReviewState = ReviewState.Unreviewed,
            AiReadingReview = new("applied", "AI 读音已应用，可直接打轴或继续编辑。",
                source.PrimaryText, proposal.Text, proposal.Segments.Select(s => new ReadingSuggestion(s.Surface, s.Reading, s.Language)).ToArray(), []) };
    }

    public static LyricLine Failed(LyricLine line, string reason, ReadingProposal? proposal = null) => line with
    {
        AiReadingReview = new(proposal is null ? "failed" : "protected", reason, line.PrimaryText,
            proposal?.Text, proposal?.Segments.Select(s => new ReadingSuggestion(s.Surface, s.Reading, s.Language)).ToArray() ?? [],
            line.PrimaryText.Length == 0 ? [] : [new(new(0, line.PrimaryText.Length), reason)])
    };
}
