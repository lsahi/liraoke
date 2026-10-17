using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Languages;

public static class LanguagePresentation
{
    public static LyricLine CreateLine(LanguageAnalysisContext context) => new(Guid.NewGuid(), context.Text,
        AnnotationType.None, null, null, context.Timing, [], ReviewState.Unreviewed, null);
    public static LanguageAnalysisResult AnalyzePlain(LanguageAnalysisContext context, LyricLanguage language, string id)
    {
        var line = CreateLine(context) with { Units = LyricImportService.CreateUnits(context.Text, context.Timing, language)
            .Select(unit => unit with { LanguageId = id }).ToArray() };
        var presentation = BuildPlain(line);
        return new(line.Units, presentation.RecordingCells, presentation.SubtitleRuns);
    }
    public static LyricPresentationSnapshot BuildPlain(LyricLine line) => new(line.PrimaryText,
        line.Units.Select(unit => new LyricRecordingCell(unit.Id,
            string.Concat(unit.DisplayRanges.Select(range => line.PrimaryText[range.Start..range.EndExclusive])))).ToArray(),
        line.AnnotationType == AnnotationType.Pinyin && !string.IsNullOrWhiteSpace(line.AnnotationText) && line.PrimaryText.Length > 0
        ? [new(new(0, line.PrimaryText.Length), line.AnnotationText, null, line.Units.Select(unit => unit.Id).ToArray(), LanguageIds.Chinese)] :
        JapaneseRubyBuilder.BuildTimingGroups(line, false).Select(group => new LyricSubtitleRun(group.Range,
            group.UnitIndexes.Count > 0 && group.UnitIndexes.All(index => line.Units[index].LanguageId == LanguageIds.Chinese)
                && string.Concat(group.UnitIndexes.Select(index => line.Units[index].Reading)) != group.Text
                ? string.Join(" ", group.UnitIndexes.Select(index => line.Units[index].Reading)) : "",
            null, group.UnitIndexes.Select(index => line.Units[index].Id).ToArray(),
            group.UnitIndexes.Count == 0 ? null : line.Units[group.UnitIndexes[0]].LanguageId)).ToArray());
    public static LyricPresentationSnapshot BuildJapanese(LyricLine line) => new(line.PrimaryText,
        line.Units.Select(unit => new LyricRecordingCell(unit.Id, unit.Reading,
            string.Concat(unit.DisplayRanges.Select(range => line.PrimaryText[range.Start..range.EndExclusive])), unit.Romanization)).ToArray(),
        JapaneseRubyBuilder.Build(line, false).Select(group => Run(line, group, false)).ToArray(),
        RomanizedSubtitleRuns: JapaneseRubyBuilder.Build(line, true).Select(group => Run(line, group, true)).ToArray());

    private static LyricSubtitleRun Run(LyricLine line, RubyGroup group, bool romanized) => new(group.Range,
        romanized ? "" : group.Annotation, romanized ? group.Annotation : null,
        group.UnitIndexes.Select(index => line.Units[index].Id).ToArray(), LanguageIds.Japanese);
}
