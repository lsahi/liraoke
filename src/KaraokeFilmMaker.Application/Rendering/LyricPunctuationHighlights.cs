using System.Runtime.CompilerServices;
using System.Text;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Rendering;

/// <summary>
/// Unmapped punctuation shares the preceding display group's entire sweep, without changing timing units.
/// </summary>
internal static class LyricPunctuationHighlights
{
    private static readonly ConditionalWeakTable<LyricLine, Map> Maps = new();
    public static IReadOnlyList<SubtitleHighlight> Attach(LyricLine line, IReadOnlyList<SubtitleHighlight> highlights)
    {
        var map = Maps.GetValue(line, _ => CreateMap(line, highlights));
        return highlights.Select((highlight, i) => highlight with { DisplayRanges = map.Ranges[i] }).ToArray();
    }
    private static Map CreateMap(LyricLine line, IReadOnlyList<SubtitleHighlight> highlights)
    {
        var ranges = highlights.Select(highlight => highlight.DisplayRanges.ToList()).ToArray();
        var source = highlights.SelectMany((highlight, index) => highlight.DisplayRanges.Select(range => (Range: range, Index: index))).ToArray();
        int cursor = 0;
        foreach (Rune rune in line.PrimaryText.EnumerateRunes())
        {
            int end = cursor + rune.Utf16SequenceLength;
            if (Rune.IsPunctuation(rune) && !source.Any(item => item.Range.Start < end && cursor < item.Range.EndExclusive))
            {
                var before = source.Where(item => item.Range.EndExclusive <= cursor).OrderByDescending(item => item.Range.EndExclusive).ToArray();
                var after = source.Where(item => item.Range.Start >= end).OrderBy(item => item.Range.Start).ToArray();
                if (before.Length > 0 && OnlySeparators(line.PrimaryText[before[0].Range.EndExclusive..cursor]))
                    ranges[before[0].Index].Add(new(cursor, rune.Utf16SequenceLength));
                else if (before.Length == 0 && after.Length > 0 && OnlySeparators(line.PrimaryText[end..after[0].Range.Start]))
                    ranges[after[0].Index].Add(new(cursor, rune.Utf16SequenceLength));
            }
            cursor = end;
        }
        return new(ranges.Select(Merge).ToArray());
    }
    private static bool OnlySeparators(string value) => value.EnumerateRunes().All(rune => Rune.IsWhiteSpace(rune) || Rune.IsPunctuation(rune));
    private static IReadOnlyList<TextRange> Merge(List<TextRange> ranges)
    {
        List<TextRange> result = [];
        foreach (var range in ranges.OrderBy(range => range.Start))
        {
            if (result.Count > 0 && range.Start <= result[^1].EndExclusive)
                result[^1] = new(result[^1].Start, Math.Max(result[^1].EndExclusive, range.EndExclusive) - result[^1].Start);
            else result.Add(range);
        }
        return result;
    }
    private sealed record Map(IReadOnlyList<IReadOnlyList<TextRange>> Ranges);
}
