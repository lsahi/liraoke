using System.Text;
using KaraokeFilmMaker.Application.Japanese;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Rendering;

public sealed record RubyGroup(TextRange Range, string Text, string Annotation, IReadOnlyList<int> UnitIndexes);

public static class JapaneseRubyBuilder
{
    // Subtitle ruby follows written word boundaries, not the grouping used to tap syllables.
    // Romaji remains a pronunciation reference for entire singing units.
    public static IReadOnlyList<RubyGroup> Build(LyricLine line, bool showRomanization)
    {
        var groups = BuildTimingGroups(line, showRomanization);
        return showRomanization ? groups : groups.SelectMany(group => JapaneseSubtitleReading.Split(line, group)).ToArray();
    }

    // Overlapping mappings are one display group (e.g. 一緒 with いっ / しょ), never repeated kanji.
    public static IReadOnlyList<RubyGroup> BuildTimingGroups(LyricLine line, bool showRomanization)
    {
        var spans = line.Units.SelectMany((unit, index) => unit.DisplayRanges
            .Where(range => range.EndExclusive <= line.PrimaryText.Length)
            .Select(range => (Range: range, Index: index))).OrderBy(item => item.Range.Start).ToArray();
        List<(int Start, int End, HashSet<int> Indexes)> groups = [];
        foreach (var span in spans)
        {
            if (groups.Count > 0 && span.Range.Start < groups[^1].End)
            {
                var previous = groups[^1]; previous.Indexes.Add(span.Index);
                groups[^1] = (previous.Start, Math.Max(previous.End, span.Range.EndExclusive), previous.Indexes);
            }
            else { groups.Add((span.Range.Start, span.Range.EndExclusive, [span.Index])); }
        }
        List<RubyGroup> result = [];
        int cursor = 0;
        foreach (var group in groups)
        {
            if (group.Start > cursor) { AddPlain(cursor, group.Start); }
            int[] indexes = group.Indexes.Order().ToArray();
            string text = line.PrimaryText[group.Start..group.End];
            string annotation = showRomanization
                ? string.Join(" ", indexes.Select(index => Romaji(line, index)))
                : ContainsKanji(text) ? string.Concat(indexes.Select(index => line.Units[index].Reading)) : "";
            if (!showRomanization && ContainsKanji(annotation)) { annotation = ""; }
            result.Add(new(new TextRange(group.Start, group.End - group.Start), text, annotation, indexes));
            cursor = group.End;
        }
        if (cursor < line.PrimaryText.Length) { AddPlain(cursor, line.PrimaryText.Length); }
        return result;

        void AddPlain(int start, int end) => result.Add(new(new TextRange(start, end - start), line.PrimaryText[start..end], "", []));
    }

    public static string Romaji(LyricLine line, int index) => !string.IsNullOrWhiteSpace(line.Units[index].Romanization)
        ? line.Units[index].Romanization!
        : KanaRomanizer.RomanizeSingingUnit(line.Units[index].Reading, index + 1 < line.Units.Count ? line.Units[index + 1].Reading : null);

    public static bool ContainsKanji(string text) => text.EnumerateRunes().Any(rune => rune.Value
        is >= 0x3400 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF);
}
