using System.Runtime.CompilerServices;
using System.Text;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Rendering;

/// <summary>
/// Punctuation has no singing unit of its own. Its display-only completion follows the next
/// mapped unit's start, or the last sung unit's end for trailing punctuation. Both preview and
/// export consume these highlights, independently of language/presentation plugins.
/// </summary>
internal static class LyricPunctuationHighlights
{
    // Edited/retimed lines are new immutable records. Avoid re-scanning Unicode and mappings
    // on every preview frame while letting old line instances be collected normally.
    private static readonly ConditionalWeakTable<LyricLine, PunctuationMap> Maps = new();

    public static IEnumerable<SubtitleHighlight> Build(LyricLine line, ProjectTime time) =>
        Maps.GetValue(line, CreateMap).Entries
            .Where(entry => time >= entry.CompletedAt)
            .Select(entry => new SubtitleHighlight([entry.Range], 1));

    private static PunctuationMap CreateMap(LyricLine line)
    {
        var mappings = line.Units.SelectMany(unit => unit.DisplayRanges
            .Where(range => range.Start >= 0 && range.Length > 0 && range.EndExclusive <= line.PrimaryText.Length)
            .Select(range => (Range: range, Start: unit.Timing.Start)))
            .OrderBy(mapping => mapping.Range.Start).ThenBy(mapping => mapping.Start).ToArray();
        if (mappings.Length == 0) { return new([]); }

        ProjectTime completed = line.Units.Max(unit => unit.Timing.End);
        List<Entry> entries = [];
        int cursor = 0;
        foreach (Rune rune in line.PrimaryText.EnumerateRunes())
        {
            int end = cursor + rune.Utf16SequenceLength;
            // Apostrophes/hyphens inside a mapped word retain that word's continuous sweep.
            // Only unmapped punctuation is completed automatically; never uncovered lyrics.
            if (Rune.IsPunctuation(rune) && !mappings.Any(mapping =>
                mapping.Range.Start < end && mapping.Range.EndExclusive > cursor))
            {
                var following = Array.FindIndex(mappings, mapping => mapping.Range.Start >= end);
                entries.Add(new(new TextRange(cursor, rune.Utf16SequenceLength),
                    following >= 0 ? mappings[following].Start : completed));
            }
            cursor = end;
        }
        return new(entries);
    }

    private sealed record Entry(TextRange Range, ProjectTime CompletedAt);
    private sealed record PunctuationMap(IReadOnlyList<Entry> Entries);
}
