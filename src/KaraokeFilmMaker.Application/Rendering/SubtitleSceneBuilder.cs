using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;
using System.Runtime.CompilerServices;
using System.Text;

namespace KaraokeFilmMaker.Application.Rendering;

public enum SubtitleSlot
{
    TopLeft,
    BottomRight,
}

public sealed record SubtitleHighlight(IReadOnlyList<TextRange> DisplayRanges, double Progress);

public sealed record SubtitleLineScene(
    LyricLine Line,
    SubtitleSlot Slot,
    bool IsActive,
    IReadOnlyList<SubtitleHighlight> Highlights,
    double AuxiliaryProgress,
    LyricLanguage Language = LyricLanguage.Chinese,
    IReadOnlyDictionary<Guid, double>? UnitProgress = null,
    double? EntranceProgress = null);

public sealed record SubtitleScene(IReadOnlyList<SubtitleLineScene> Lines);

public sealed class SubtitleSceneBuilder
{
    public const double PreviewLeadSeconds = 6;
    public SubtitleScene Build(LyricDocument document, ProjectTime time)
    {
        var timed = document.Lines.Select((line, index) => (Line: line, Index: index))
            .Where(item => !item.Line.IsInstrumental && !string.IsNullOrWhiteSpace(item.Line.PrimaryText)
                && item.Line.Timing.Duration.Microseconds > 0)
            .OrderBy(item => item.Line.Timing.Start).ThenBy(item => item.Index).ToArray();
        List<SubtitleLineScene> visible = [];
        int active = Array.FindLastIndex(timed, item => item.Line.Timing.Start <= time && time < item.Line.Timing.End);
        if (active >= 0)
        {
            Add(timed[active].Line, active, isActive: true);
            if (active + 1 < timed.Length)
            {
                Add(timed[active + 1].Line, active + 1, isActive: false);
            }
        }
        else
        {
            int upcoming = Array.FindIndex(timed, item => item.Line.Timing.Start > time);
            if (upcoming >= 0)
            {
                Add(timed[upcoming].Line, upcoming, isActive: false);
                if (upcoming + 1 < timed.Length)
                {
                    Add(timed[upcoming + 1].Line, upcoming + 1, isActive: false);
                }
            }
        }

        return new SubtitleScene(visible);

        void Add(LyricLine line, int ordinal, bool isActive)
        {
            double untilStart = line.Timing.Start.TotalSeconds - time.TotalSeconds;
            if (!isActive && untilStart > PreviewLeadSeconds) { return; }
            // Use the latest preceding end, not just the previous row: overlapping lines are not silence.
            double precedingEnd = timed.Take(ordinal).Select(item => item.Line.Timing.End.TotalSeconds).DefaultIfEmpty(0).Max();
            bool hasEntrance = line.Timing.Start.TotalSeconds - precedingEnd >= PreviewLeadSeconds;
            visible.Add(new SubtitleLineScene(
            line,
            ordinal % 2 == 0 ? SubtitleSlot.TopLeft : SubtitleSlot.BottomRight,
            isActive,
            isActive ? BuildHighlights(line, time) : [],
            isActive && line.IsHighlightEnabled ? AuxiliaryProgress(line, time) : 0,
            document.Language,
            isActive && line.IsHighlightEnabled && LyricPresentationLayout.HasCurrentSnapshot(line)
                ? line.Units.ToDictionary(unit => unit.Id, unit => Progress(unit.Timing, time)) : null,
            hasEntrance ? Math.Clamp(1 - untilStart / PreviewLeadSeconds, 0, 1) : null));
        }
    }

    public IReadOnlyList<SubtitleHighlight> BuildHighlights(LyricLine line, ProjectTime time)
    {
        if (!line.IsHighlightEnabled || line.IsInstrumental || line.PrimaryText.Length == 0)
        {
            return [];
        }

        if (line.Units.Count == 0)
        {
            if (line.PrimaryText.EnumerateRunes().All(rune => Rune.IsPunctuation(rune) || Rune.IsWhiteSpace(rune)))
            {
                return [];
            }
            return [new SubtitleHighlight([new TextRange(0, line.PrimaryText.Length)], Progress(line.Timing, time))];
        }

        // A shared mapping is a single display group, regardless of its number of singing mora.
        // Each mora advances its share of the group's width using its own timing.
        var highlights = line.Units.Where(unit => unit.DisplayRanges.Count > 0)
            .GroupBy(unit => string.Join(";", unit.DisplayRanges.Distinct().OrderBy(range => range.Start)
                .ThenBy(range => range.Length).Select(range => $"{range.Start}:{range.Length}")))
            .Select(group => new SubtitleHighlight(
                group.First().DisplayRanges.Distinct().OrderBy(range => range.Start).ThenBy(range => range.Length).ToArray(),
                group.Average(unit => Progress(unit.Timing, time))))
            .ToList();
        highlights.AddRange(LyricPunctuationHighlights.Build(line, time));
        return highlights;
    }

    private static double Progress(TimeRange timing, ProjectTime time)
    {
        if (time < timing.Start)
        {
            return 0;
        }

        if (time >= timing.End)
        {
            return 1;
        }

        return (double)(time.Microseconds - timing.Start.Microseconds) / timing.Duration.Microseconds;
    }

    private static double AuxiliaryProgress(LyricLine line, ProjectTime time)
    {
        if (line.Units.Count == 0)
        {
            return Progress(line.Timing, time);
        }

        double total = line.Units.Sum(unit => Math.Max(1, unit.Reading.Length));
        return line.Units.Sum(unit => Progress(unit.Timing, time) * Math.Max(1, unit.Reading.Length)) / total;
    }
}

/// <summary>Consumes persisted display instructions only; never invokes a language plugin during playback.</summary>
public static class LyricPresentationLayout
{
    private static readonly ConditionalWeakTable<LyricLine, SnapshotValidity> SnapshotValidation = new();

    public static bool HasCurrentSnapshot(LyricLine line) => line.Presentation is { FormatVersion: 1 } snapshot
        && string.Equals(snapshot.SourceText, line.PrimaryText, StringComparison.Ordinal)
        && SnapshotValidation.GetValue(line, static value =>
        {
            // Lines are immutable records. Validate unit references once per edited line, not per frame.
            var ids = value.Units.Select(unit => unit.Id).ToHashSet();
            var presentation = value.Presentation!;
            return new(presentation.RecordingCells.All(cell => ids.Contains(cell.UnitId))
                && presentation.SubtitleRuns.Concat(presentation.RomanizedSubtitleRuns ?? [])
                    .All(run => run.UnitIds.All(ids.Contains)));
        }).IsValid;

    public static IReadOnlyList<RubyGroup> BuildSubtitleGroups(LyricLine line, bool showRomanization)
    {
        if (!HasCurrentSnapshot(line)) { return []; }
        var snapshot = line.Presentation!;
        var runs = showRomanization ? snapshot.RomanizedSubtitleRuns ?? snapshot.SubtitleRuns : snapshot.SubtitleRuns;
        var indexes = line.Units.Select((unit, index) => (unit.Id, Index: index)).ToDictionary(item => item.Id, item => item.Index);
        List<RubyGroup> groups = [];
        int cursor = 0;
        foreach (var run in runs.OrderBy(run => run.Range.Start))
        {
            // A malformed or unsupported plugin snapshot must not erase text or break rendering.
            if (run.Range.Start < cursor || run.Range.EndExclusive > line.PrimaryText.Length) { continue; }
            if (run.Range.Start > cursor) { AddPlain(cursor, run.Range.Start); }
            string annotation = showRomanization ? run.Romanization ?? run.Annotation : run.Annotation;
            groups.Add(new(run.Range, line.PrimaryText.Substring(run.Range.Start, run.Range.Length), annotation,
                run.UnitIds.Where(indexes.ContainsKey).Distinct().Select(id => indexes[id]).ToArray()));
            cursor = run.Range.EndExclusive;
        }
        if (cursor < line.PrimaryText.Length) { AddPlain(cursor, line.PrimaryText.Length); }
        return groups;

        void AddPlain(int start, int end) => groups.Add(new(new TextRange(start, end - start),
            line.PrimaryText[start..end], "", []));
    }

    private sealed record SnapshotValidity(bool IsValid);
}
