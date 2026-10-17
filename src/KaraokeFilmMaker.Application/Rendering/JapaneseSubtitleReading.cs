using System.Globalization;
using System.Text;
using KaraokeFilmMaker.Application.Japanese;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Rendering;

/// <summary>Derives display-only ruby from existing readings without modifying timing units or saved data.</summary>
internal static class JapaneseSubtitleReading
{
    public static IReadOnlyList<RubyGroup> Split(LyricLine line, RubyGroup group)
    {
        if (group.Annotation.Length == 0 || !JapaneseRubyBuilder.ContainsKanji(group.Text)) { return [group]; }
        var parts = new List<(int Start, int Length, bool Kanji)>();
        int cursor = 0;
        foreach (var rune in group.Text.EnumerateRunes())
        {
            bool kanji = JapaneseRubyBuilder.ContainsKanji(rune.ToString()) || rune.Value is 0x3005 or 0x30F5 or 0x30F6 or 0x3095 or 0x3096;
            if (parts.Count > 0 && parts[^1].Kanji == kanji)
            { var previous = parts[^1]; parts[^1] = (previous.Start, previous.Length + rune.Utf16SequenceLength, kanji); }
            else { parts.Add((cursor, rune.Utf16SequenceLength, kanji)); }
            cursor += rune.Utf16SequenceLength;
        }
        if (parts.Count == 1) { return [group]; }

        string Normalize(string value) => KanaRomanizer.ToHiragana(value.Normalize(NormalizationForm.FormKC));
        string reading = Normalize(group.Annotation);
        string[] literals = parts.Select(part => Normalize(group.Text.Substring(part.Start, part.Length))).ToArray();
        int[] boundaries = StringInfo.ParseCombiningCharacters(reading).Append(reading.Length).ToArray();
        var memo = new Dictionary<(int Part, int Offset), (int Count, int End)>();

        // Literal kana anchor the reading. Accept only one complete alignment; never guess an
        // ambiguous kanji reading or strip っ/ん globally (一緒=いっしょ and 本=ほん must stay intact).
        (int Count, int End) Solve(int part, int offset)
        {
            if (part == parts.Count) { return (offset == reading.Length ? 1 : 0, offset); }
            if (memo.TryGetValue((part, offset), out var cached)) { return cached; }
            int count = 0, chosenEnd = offset;
            if (!parts[part].Kanji)
            {
                string literal = literals[part];
                if (reading.AsSpan(offset).StartsWith(literal.AsSpan(), StringComparison.Ordinal))
                { chosenEnd = offset + literal.Length; count = Solve(part + 1, chosenEnd).Count; }
            }
            else
            {
                foreach (int end in boundaries.Where(end => end > offset))
                {
                    int matches = Solve(part + 1, end).Count;
                    if (matches == 0) { continue; }
                    chosenEnd = end; count = Math.Min(2, count + matches);
                    if (count == 2) { break; }
                }
            }
            return memo[(part, offset)] = (count, chosenEnd);
        }
        if (Solve(0, 0).Count != 1) { return [group]; }
        var result = new List<RubyGroup>();
        int readingOffset = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i]; int end = Solve(i, readingOffset).End;
            var range = new TextRange(group.Range.Start + part.Start, part.Length);
            int[] units = group.UnitIndexes.Where(index => line.Units[index].DisplayRanges.Any(mapped =>
                mapped.Start < range.EndExclusive && mapped.EndExclusive > range.Start)).ToArray();
            result.Add(new(range, group.Text.Substring(part.Start, part.Length),
                part.Kanji ? reading[readingOffset..end] : "", units));
            readingOffset = end;
        }
        return result;
    }
}
