using System.Text;
using KaraokeFilmMaker.Application.Japanese;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Lyrics;

/// <summary>Small corrections keep unit identity and timing, including pending in-memory taps.</summary>
public static class LyricUnitEditor
{
    public static RubyGroup GetGroup(LyricLine line, Guid unitId)
    {
        int index = line.Units.ToList().FindIndex(unit => unit.Id == unitId);
        var groups = JapaneseRubyBuilder.BuildTimingGroups(line, false).Where(group => group.UnitIndexes.Contains(index)).ToArray();
        if (groups.Length != 1) { throw new InvalidOperationException("此单元映射到多个分离文字区域，请使用完整单元编辑器修改。"); }
        return groups[0];
    }

    public static LyricLine Edit(LyricLine line, Guid unitId, string text, string reading, LyricLanguage language)
    {
        var group = GetGroup(line, unitId);
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(reading)
            || text.IndexOfAny(['\r', '\n']) >= 0 || reading.IndexOfAny(['\r', '\n']) >= 0)
        { throw new InvalidOperationException("原文和读音不能为空或包含换行；换行请使用下方专用按钮。"); }
        int selected = line.Units.ToList().FindIndex(unit => unit.Id == unitId);
        if (line.Units[selected].LanguageId is { } languageId)
        { language = LanguageIds.ToLegacy(languageId); }
        bool japanese = language == LyricLanguage.Japanese;
        reading = reading.Trim();
        if (japanese)
        {
            reading = KanaRomanizer.ToHiragana(reading.Normalize(NormalizationForm.FormKC));
            if (JapaneseRubyBuilder.ContainsKanji(reading)
                || KanaRomanizer.SplitSingingUnits(reading).Count != 1)
            { throw new InvalidOperationException("这里只修改当前演唱单元的读音（如 ちゃん）。需要增减音节时，请使用打轴页的“编辑整句”修改读音并重新分组。"); }
            if (JapaneseRubyBuilder.ContainsKanji(text) && reading.EnumerateRunes().Any(rune => rune.Value is not (>= 0x3041 and <= 0x3096 or 0x30FC)))
            { throw new InvalidOperationException("汉字读音请填写假名，不是罗马音。"); }
        }
        else
        {
            if (language is LyricLanguage.Chinese or LyricLanguage.English
                && LyricImportService.CreateUnits(text, line.Units[selected].Timing, language).Count != 1)
            { throw new InvalidOperationException("快捷编辑保持一个中文字 / 英文单词；增减单元请使用打轴页的“编辑整句”。"); }
            if (text != group.Text && reading == line.Units[selected].Reading) { reading = text; }
        }
        int delta = text.Length - group.Range.Length;
        var units = line.Units.Select(unit => unit with
        {
            Reading = unit.Id == unitId ? reading : unit.Reading,
            DisplayRanges = text == group.Text ? unit.DisplayRanges : unit.DisplayRanges.Select(range =>
                range.Start >= group.Range.EndExclusive ? new TextRange(range.Start + delta, range.Length)
                : range.Start >= group.Range.Start && range.EndExclusive <= group.Range.EndExclusive
                    ? new TextRange(group.Range.Start, text.Length) : range).Distinct().ToArray(),
        }).ToArray();
        if (japanese)
        {
            // The previous unit's closing consonant may depend on this pronunciation.
            for (int i = Math.Max(0, selected - 1); i <= selected; i++)
            {
                if (units[i].LanguageId is not null and not LanguageIds.Japanese) { continue; }
                units[i] = units[i] with { Romanization = KanaRomanizer.RomanizeSingingUnit(units[i].Reading,
                    i + 1 < units.Length && units[i + 1].LanguageId is null or LanguageIds.Japanese ? units[i + 1].Reading : null) };
            }
        }
        bool allJapanese = japanese && (line.LanguageSpans is null || line.LanguageSpans.All(span => span.LanguageId == LanguageIds.Japanese));
        return line with
        {
            PrimaryText = line.PrimaryText[..group.Range.Start] + text + line.PrimaryText[group.Range.EndExclusive..],
            Units = units,
            ReadingText = allJapanese ? string.Concat(units.Select(unit => unit.Reading)) : line.ReadingText,
            AnnotationText = allJapanese ? string.Concat(units.Select(unit => unit.Reading)) : line.AnnotationText,
            RomanizationText = allJapanese ? string.Join(" ", units.Select(unit => unit.Romanization)) : line.RomanizationText,
            ReadingSource = japanese ? ReadingSource.Manual : line.ReadingSource,
            LanguageSpans = line.LanguageSpans?.Select(span => span with
            {
                Range = span.Range.Start >= group.Range.EndExclusive ? new TextRange(span.Range.Start + delta, span.Range.Length)
                    : span.Range.Start <= group.Range.Start && span.Range.EndExclusive >= group.Range.EndExclusive
                        ? new TextRange(span.Range.Start, span.Range.Length + delta) : span.Range,
                ManualReading = span.Range.Start <= group.Range.Start && span.Range.EndExclusive >= group.Range.EndExclusive
                    ? null : span.ManualReading,
            }).ToArray(),
            Presentation = null,
        };
    }
}
