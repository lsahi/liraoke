using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Languages;

public static class LanguageMetadataEditor
{
    public static IReadOnlyList<LyricLanguageSpan>? SliceSpans(LyricLine line, int start, int length) =>
        line.LanguageSpans?.Where(span => span.Range.Start < start + length && span.Range.EndExclusive > start)
        .Select(span => span with { Id = start == 0 ? span.Id : Guid.NewGuid(),
            Range = new(Math.Max(span.Range.Start, start) - start, Math.Min(span.Range.EndExclusive, start + length) - Math.Max(span.Range.Start, start)),
            ManualReading = span.Range.Start < start || span.Range.EndExclusive > start + length ? null : span.ManualReading }).ToArray();

    public static LyricPresentationSnapshot? SlicePresentation(LyricLine line, int start, int length, IReadOnlyList<LyricUnit> units)
    {
        if (line.Presentation is not { } presentation || presentation.SourceText != line.PrimaryText) { return null; }
        var ids = units.Select(unit => unit.Id).ToHashSet();
        LyricSubtitleRun[] Slice(IReadOnlyList<LyricSubtitleRun> runs) => runs.Where(run => run.Range.Start < start + length && run.Range.EndExclusive > start)
            .Select(run => run with { Range = new(Math.Max(run.Range.Start, start) - start,
                Math.Min(run.Range.EndExclusive, start + length) - Math.Max(run.Range.Start, start)),
                UnitIds = run.UnitIds.Where(ids.Contains).ToArray(),
                Annotation = run.Range.Start < start || run.Range.EndExclusive > start + length ? "" : run.Annotation,
                Romanization = run.Range.Start < start || run.Range.EndExclusive > start + length ? null : run.Romanization }).ToArray();
        return new(line.PrimaryText.Substring(start, length), presentation.RecordingCells.Where(cell => ids.Contains(cell.UnitId)).ToArray(),
            Slice(presentation.SubtitleRuns), RomanizedSubtitleRuns: presentation.RomanizedSubtitleRuns is null ? null : Slice(presentation.RomanizedSubtitleRuns));
    }

    public static IReadOnlyList<LyricLanguageSpan>? MergeSpans(LyricLine first, LyricLine second)
    {
        if (first.LanguageSpans is null || second.LanguageSpans is null) { return null; }
        int shift = first.PrimaryText.Length + 1;
        var left = first.LanguageSpans.ToArray();
        if (left.Length > 0 && left[^1].Range.EndExclusive == first.PrimaryText.Length)
        { left[^1] = left[^1] with { Range = new(left[^1].Range.Start, left[^1].Range.Length + 1) }; }
        return [.. left, .. second.LanguageSpans.Select(span => span with { Range = new(span.Range.Start + shift, span.Range.Length) })];
    }

    public static LyricPresentationSnapshot? MergePresentation(LyricLine first, LyricLine second)
    {
        if (first.Presentation is not { } a || second.Presentation is not { } b
            || a.SourceText != first.PrimaryText || b.SourceText != second.PrimaryText) { return null; }
        int shift = first.PrimaryText.Length + 1;
        LyricSubtitleRun[] Join(IReadOnlyList<LyricSubtitleRun> left, IReadOnlyList<LyricSubtitleRun> right) =>
            [.. left, new(new(first.PrimaryText.Length, 1), "", null, []),
                .. right.Select(run => run with { Range = new(run.Range.Start + shift, run.Range.Length) })];
        return new(first.PrimaryText + " " + second.PrimaryText, [.. a.RecordingCells, .. b.RecordingCells],
            Join(a.SubtitleRuns, b.SubtitleRuns), RomanizedSubtitleRuns: Join(a.RomanizedSubtitleRuns ?? a.SubtitleRuns,
                b.RomanizedSubtitleRuns ?? b.SubtitleRuns));
    }
}
