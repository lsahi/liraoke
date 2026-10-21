using System.Text;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Application.Lyrics;

namespace KaraokeFilmMaker.Application.Japanese;

public sealed class JapaneseAnnotationService(IJapaneseReadingService readingService)
{
    public async Task<LyricDocument> AnnotateAsync(
        LyricDocument document,
        CancellationToken cancellationToken = default)
    {
        if (document.Language != LyricLanguage.Japanese)
        {
            return document;
        }

        List<LyricLine> lines = [];
        foreach (LyricLine line in document.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines.Add(await AnnotateLineAsync(line, cancellationToken).ConfigureAwait(false));
        }

        return document with { Lines = lines };
    }

    public async Task<LyricLine> AnnotateLineAsync(
        LyricLine line,
        CancellationToken cancellationToken = default)
    {
        if (line.ReadingSource is ReadingSource.Confirmed or ReadingSource.Manual
            || line.Units.Any(unit => unit.IsConfirmed || unit.RawInputTime is not null)
            || string.IsNullOrWhiteSpace(line.PrimaryText))
        {
            return line;
        }

        IReadOnlyList<JapaneseToken> tokens = await readingService
            .AnalyzeAsync(line.PrimaryText, cancellationToken)
            .ConfigureAwait(false);
        if (tokens.Count == 0)
        {
            return line;
        }

        IReadOnlyList<LyricUnit> units = BuildUnits(tokens, line.Timing);

        string readingText = string.Concat(tokens.Select(token => token.Reading));
        string romanizationText = string.Join(
            ' ',
            tokens.Select(token => token.Romanization).Where(value => value.Length > 0));

        return line with
        {
            AnnotationType = AnnotationType.Kana,
            ExplicitEndTime = null,
            AnnotationText = readingText,
            ReadingText = readingText,
            RomanizationText = romanizationText,
            ReadingSource = ReadingSource.Generated,
            Units = RoleAssignments.Transfer(line, units),
            ReviewState = readingText.EnumerateRunes().Any(rune =>
                rune.Value is >= 0x3400 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF)
                ? ReviewState.ReviewLater : ReviewState.Unreviewed,
        };
    }

    public static LyricLine ApplyManualReading(LyricLine line, string reading)
    {
        if (line.PrimaryText.Length == 0) { return line; }
        reading = KanaRomanizer.ToHiragana(reading.Normalize(NormalizationForm.FormKC));
        var token = new JapaneseToken(line.PrimaryText, reading, KanaRomanizer.Romanize(reading), 0, line.PrimaryText.Length, true);
        var units = BuildUnits([token], line.Timing);
        return line with
        {
            ReadingText = reading, AnnotationText = reading,
            ExplicitEndTime = null,
            RomanizationText = string.Join(" ", units.Select(unit => unit.Romanization)),
            Units = RoleAssignments.Transfer(line, units), ReadingSource = ReadingSource.Manual,
        };
    }

    public static string? GetRecordingIssue(LyricDocument document, int firstLine = 0)
    {
        if (document.Language != LyricLanguage.Japanese) { return null; }
        for (int index = firstLine; index < document.Lines.Count; index++)
        {
            var line = document.Lines[index];
            // Mixed-language documents are validated from their persisted per-span analysis.
            if (line.LanguageSpans is not null) { continue; }
            if (line.IsInstrumental || !line.IsHighlightEnabled || string.IsNullOrWhiteSpace(line.PrimaryText)) { continue; }
            bool unresolved = line.Units.Any(unit => string.IsNullOrWhiteSpace(unit.Reading)
                || unit.Reading.EnumerateRunes().Any(rune => IsKanji(rune.Value) || rune.Value is 0x3095 or 0x3096 or 0x30F5 or 0x30F6));
            bool uncoveredKanji = line.PrimaryText.EnumerateRunes().Any(rune => IsKanji(rune.Value))
                && (line.ReadingSource is null || string.IsNullOrWhiteSpace(line.ReadingText));
            int textPosition = 0;
            foreach (var rune in line.PrimaryText.EnumerateRunes())
            {
                if (IsKanji(rune.Value) && !line.Units.Any(unit => unit.DisplayRanges.Any(range =>
                    range.Start <= textPosition && range.EndExclusive >= textPosition + rune.Utf16SequenceLength)))
                { uncoveredKanji = true; }
                textPosition += rune.Utf16SequenceLength;
            }
            if (unresolved || uncoveredKanji)
            {
                return $"第 {index + 1} 句尚未完成日语读音：请先生成假名，再校正专名 / 当字及未识别的读音，才能开始打轴。罗马音仅供参考。";
            }
        }
        return null;
    }

    private static bool IsKanji(int value) => value is >= 0x3400 and <= 0x9FFF
        or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF;

    private static IReadOnlyList<LyricUnit> BuildUnits(IReadOnlyList<JapaneseToken> tokens, TimeRange timing)
    {
        var reading = new StringBuilder();
        List<TextRange> mapping = [];
        Dictionary<(int Start, int Length), string> tokenRomanizations = [];
        foreach (var token in tokens)
        {
            string kana = KanaRomanizer.ToHiragana(token.Reading.Normalize(NormalizationForm.FormKC));
            string surface = KanaRomanizer.ToHiragana(token.Surface);
            int prefix = 0, suffix = 0;
            while (prefix < Math.Min(surface.Length, kana.Length) && surface[prefix] == kana[prefix]) { prefix++; }
            while (suffix < Math.Min(surface.Length, kana.Length) - prefix
                && surface[^(suffix + 1)] == kana[^(suffix + 1)]) { suffix++; }
            tokenRomanizations[(reading.Length, kana.Length)] = token.Romanization;
            for (int i = 0; i < kana.Length; i++)
            {
                mapping.Add(i < prefix ? new TextRange(token.TextStart + i, 1)
                    : i >= kana.Length - suffix ? new TextRange(token.TextStart + token.TextLength - (kana.Length - i), 1)
                    : token.TextLength > prefix + suffix
                        ? new TextRange(token.TextStart + prefix, token.TextLength - prefix - suffix)
                        : new TextRange(token.TextStart, token.TextLength));
            }
            reading.Append(kana);
        }

        // Group the full reading, not individual dictionary tokens: ん/っ may cross token boundaries.
        return LyricImportService.CreateUnits(reading.ToString(), timing, LyricLanguage.Japanese).Select(unit =>
        {
            TextRange range = unit.DisplayRanges[0];
            var ranges = mapping.Skip(range.Start).Take(range.Length).Distinct().OrderBy(item => item.Start).ToArray();
            List<TextRange> merged = [];
            foreach (var item in ranges)
            {
                if (merged.Count > 0 && item.Start <= merged[^1].EndExclusive)
                {
                    var previous = merged[^1];
                    merged[^1] = new TextRange(previous.Start, Math.Max(previous.EndExclusive, item.EndExclusive) - previous.Start);
                }
                else { merged.Add(item); }
            }
            string? romanization = tokenRomanizations.TryGetValue((range.Start, range.Length), out var tokenRomaji)
                && unit.Reading is "へ" or "は" or "を"
                && !string.IsNullOrWhiteSpace(tokenRomaji) ? tokenRomaji : unit.Romanization;
            return unit with { DisplayRanges = merged, Romanization = romanization, LanguageId = LanguageIds.Japanese };
        }).ToArray();
    }
}
