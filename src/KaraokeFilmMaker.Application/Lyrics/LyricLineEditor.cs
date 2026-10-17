using System.Globalization;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Application.Languages;

namespace KaraokeFilmMaker.Application.Lyrics;

public static class LyricLineEditor
{
    public static (LyricLine Left, LyricLine Right) SplitBeforeUnit(LyricLine line, int unitIndex)
    {
        if (unitIndex <= 0 || unitIndex >= line.Units.Count)
        { throw new InvalidOperationException("请选择句中一个单元，在它前面换行；句首不能再次换行。"); }
        var leftUnits = line.Units.Take(unitIndex).ToArray();
        var rightUnits = line.Units.Skip(unitIndex).ToArray();
        int offset = rightUnits.SelectMany(unit => unit.DisplayRanges).Min(range => range.Start);
        if (offset <= 0 || offset >= line.PrimaryText.Length
            || leftUnits.SelectMany(unit => unit.DisplayRanges).Any(range => range.EndExclusive > offset))
        { throw new InvalidOperationException("此处位于共享汉字词组内部，请选择整个词组前后换行，或先在单元编辑器调整文字映射。"); }
        ProjectTime split = rightUnits[0].Timing.Start;
        LyricLine Part(string text, IReadOnlyList<LyricUnit> units, TimeRange timing, bool left) => line with
        {
            Id = left ? line.Id : Guid.NewGuid(), PrimaryText = text, Units = units, Timing = timing,
            ReadingText = line.ReadingText is null ? null : string.Concat(units.Select(unit => unit.Reading)),
            AnnotationText = line.ReadingText is null ? null : string.Concat(units.Select(unit => unit.Reading)),
            RomanizationText = line.RomanizationText is null ? null : string.Join(" ", units.Select(unit => unit.Romanization)),
            TranslationText = left ? line.TranslationText : null,
            LanguageSpans = LanguageMetadataEditor.SliceSpans(line, left ? 0 : offset, text.Length),
            Presentation = LanguageMetadataEditor.SlicePresentation(line, left ? 0 : offset, text.Length, units),
        };
        return (Part(line.PrimaryText[..offset], leftUnits, new TimeRange(line.Timing.Start, split), true),
            Part(line.PrimaryText[offset..], rightUnits.Select(unit => unit with
            {
                DisplayRanges = unit.DisplayRanges.Select(range => new TextRange(range.Start - offset, range.Length)).ToArray(),
            }).ToArray(), new TimeRange(split, line.Timing.End), false));
    }

    public static LyricLine Merge(LyricLine first, LyricLine second)
    {
        if (second.Timing.Start < first.Timing.End)
        {
            throw new InvalidOperationException("合并前请将两行时间调整为先后顺序，不能重叠。");
        }
        int shift = first.PrimaryText.Length + 1;
        return first with
        {
            PrimaryText = first.PrimaryText + " " + second.PrimaryText,
            ReadingText = Join(first.ReadingText, second.ReadingText),
            AnnotationText = Join(first.AnnotationText, second.AnnotationText),
            RomanizationText = Join(first.RomanizationText, second.RomanizationText),
            TranslationText = Join(first.TranslationText, second.TranslationText),
            Timing = new TimeRange(first.Timing.Start, second.Timing.End),
            Units = [.. first.Units, .. second.Units.Select(unit => unit with
            {
                DisplayRanges = unit.DisplayRanges.Select(range => new TextRange(range.Start + shift, range.Length)).ToArray(),
            })],
            ReviewState = ReviewState.Unreviewed,
            LanguageSpans = LanguageMetadataEditor.MergeSpans(first, second),
            Presentation = LanguageMetadataEditor.MergePresentation(first, second),
        };
    }

    public static (LyricLine Left, LyricLine Right) Split(LyricLine line, int offset, LyricLanguage language)
    {
        if (offset <= 0 || offset >= line.PrimaryText.Length || !StringInfo.ParseCombiningCharacters(line.PrimaryText).Contains(offset))
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "拆分点必须在两个完整字符之间。");
        }
        ProjectTime split = new(line.Timing.Start.Microseconds
            + (long)((decimal)line.Timing.Duration.Microseconds * offset / line.PrimaryText.Length));
        LyricLine Part(string text, TimeRange timing, bool keepId) => line with
        {
            Id = keepId ? line.Id : Guid.NewGuid(), PrimaryText = text, Timing = timing,
            Units = LyricImportService.CreateUnits(text, timing, language),
            ReadingText = null, AnnotationText = null, RomanizationText = null, TranslationText = null,
            ReadingSource = null, ReviewState = ReviewState.Unreviewed,
            LanguageSpans = null, Presentation = null,
        };
        return (Part(line.PrimaryText[..offset], new TimeRange(line.Timing.Start, split), true),
            Part(line.PrimaryText[offset..], new TimeRange(split, line.Timing.End), false));
    }

    public static LyricLine Offset(LyricLine line, long microseconds) => line with
    {
        Timing = new TimeRange(new ProjectTime(checked(line.Timing.Start.Microseconds + microseconds)),
            new ProjectTime(checked(line.Timing.End.Microseconds + microseconds))),
        Units = line.Units.Select(unit => unit with
        {
            Timing = new TimeRange(new ProjectTime(checked(unit.Timing.Start.Microseconds + microseconds)),
                new ProjectTime(checked(unit.Timing.End.Microseconds + microseconds))),
        }).ToArray(),
    };

    private static string? Join(string? first, string? second) =>
        string.IsNullOrEmpty(first) && string.IsNullOrEmpty(second) ? null : string.Join(" ", new[] { first, second }.Where(value => !string.IsNullOrEmpty(value)));
}
