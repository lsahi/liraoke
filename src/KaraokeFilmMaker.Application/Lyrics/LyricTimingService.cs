using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Lyrics;

public sealed class LyricTimingService
{
    /// <summary>Discard timing only, retaining all edited text, readings, identities and presentation mappings.</summary>
    public static LyricDocument ClearAllTimings(LyricDocument document) => document with
    {
        Lines = document.Lines.Select(line => line with
        {
            Timing = new TimeRange(ProjectTime.Zero, ProjectTime.Zero),
            ExplicitEndTime = null,
            Units = line.Units.Select(unit => unit with
            {
                Timing = new TimeRange(ProjectTime.Zero, ProjectTime.Zero),
                RawInputTime = null,
                IsConfirmed = false,
            }).ToArray(),
        }).ToArray(),
    };

    /// <summary>Records a continuous stream of singing units without a preceding line-timing pass.</summary>
    public LyricDocument ApplyContinuousUnitMarkers(LyricDocument document, IReadOnlyList<ProjectTime> markers,
        ProjectTime songEnd, int firstLineIndex, ProjectTime recordingEnd, IReadOnlyList<ProjectTime>? rawMarkers = null,
        bool onlyUnconfirmed = false, bool explicitLineEnd = false)
    {
        IReadOnlyList<UnitTapTarget> plan = UnitTapPlan.Build(document, firstLineIndex, onlyUnconfirmed);
        if (plan.Count == 0 || markers.Count == 0 || markers.Count > plan.Count + 1)
        { throw new ArgumentException("请为每个演唱单元记录入口时间；最后可额外记录一个结束点。", nameof(markers)); }
        if (rawMarkers is not null && rawMarkers.Count != markers.Count)
        { throw new ArgumentException("原始与修正时间点数量不一致。", nameof(rawMarkers)); }
        bool hasEndMarker = markers.Count == plan.Count + 1;
        int unitCount = hasEndMarker ? plan.Count : markers.Count;
        ProjectTime end = hasEndMarker ? markers[^1] : recordingEnd;
        if (end > songEnd || end <= markers[unitCount - 1])
        { throw new ArgumentException("结束时间必须晚于最后一个字的入口，且不能超过歌曲终点。"); }
        for (int index = 0; index < unitCount; index++)
        {
            if (markers[index] >= songEnd || index > 0 && markers[index] <= markers[index - 1])
            { throw new ArgumentException("逐字时间点必须严格递增且位于歌曲范围内。"); }
        }
        if (unitCount < plan.Count && plan[unitCount - 1].LineId == plan[unitCount].LineId)
        { throw new ArgumentException("当前句还有字未记录。请打完当前句，或按退格撤销；不会用均分时间代替真实打点。"); }
        LyricDocument result;
        if (onlyUnconfirmed)
        { result = ApplyRemainingUnitMarkers(document, plan, markers, rawMarkers, unitCount, end, explicitLineEnd); }
        else
        {
            LyricLine[] lines = document.Lines.ToArray();
            int cursor = 0;
            foreach (var group in plan.Take(unitCount).GroupBy(target => target.LineIndex))
            {
                int count = group.Count();
                ProjectTime lineEnd = cursor + count < unitCount ? markers[cursor + count] : end;
                LyricLine line = Retiming(lines[group.Key], new TimeRange(markers[cursor], lineEnd));
                lines[group.Key] = ApplyUnitMarkers(line, markers.Skip(cursor).Take(count).ToArray(),
                    rawMarkers?.Skip(cursor).Take(count).ToArray());
                cursor += count;
            }
            result = document with { Lines = lines };
        }
        int lastLine = plan[unitCount - 1].LineIndex;
        var changedLines = plan.Take(unitCount).Select(target => target.LineIndex).ToHashSet();
        result = result with
        {
            Lines = result.Lines.Select((line, index) => index == lastLine
                ? line with { ExplicitEndTime = explicitLineEnd && line.Timing.End == end ? end : null }
                : changedLines.Contains(index)
                    ? line with { ExplicitEndTime = null } : line).ToArray(),
        };
        LyricTimingConflicts.Validate(result);
        return result;
    }

    private static LyricDocument ApplyRemainingUnitMarkers(LyricDocument document, IReadOnlyList<UnitTapTarget> plan,
        IReadOnlyList<ProjectTime> markers, IReadOnlyList<ProjectTime>? rawMarkers, int unitCount, ProjectTime end,
        bool explicitLineEnd)
    {
        // Completed units are immutable anchors, not pending markers. This also prevents a new
        // sentence's last unit from stretching across a completed sentence skipped by the plan.
        var saved = document.Lines.SelectMany((line, lineIndex) => line.IsInstrumental || !line.IsHighlightEnabled
            ? Enumerable.Empty<(int LineIndex, int UnitIndex, LyricUnit Unit)>()
            : line.Units.Select((unit, unitIndex) => (LineIndex: lineIndex, UnitIndex: unitIndex, Unit: unit))
                .Where(item => LyricRecordingProgress.IsCompleted(item.Unit))).ToArray();
        LyricLine[] lines = document.Lines.ToArray();
        int cursor = 0;
        foreach (var group in plan.Take(unitCount).GroupBy(target => target.LineIndex))
        {
            var line = lines[group.Key];
            LyricUnit[] units = line.Units.ToArray();
            foreach (var target in group)
            {
                ProjectTime start = markers[cursor];
                ProjectTime unitEnd = cursor + 1 < unitCount ? markers[cursor + 1] : end;
                if (target.UnitIndex + 1 < line.Units.Count
                    && LyricRecordingProgress.IsCompleted(line.Units[target.UnitIndex + 1]))
                { unitEnd = line.Units[target.UnitIndex + 1].Timing.Start; }
                foreach (var anchor in saved)
                {
                    bool comesBefore = anchor.LineIndex < target.LineIndex
                        || anchor.LineIndex == target.LineIndex && anchor.UnitIndex < target.UnitIndex;
                    if (comesBefore)
                    {
                        bool adjacentExplicit = anchor.LineIndex == target.LineIndex - 1
                            && LyricTimingConflicts.HasExplicitEnd(document.Lines[anchor.LineIndex]);
                        if (anchor.Unit.Timing.End > start && !adjacentExplicit)
                        { throw new ArgumentException("新的打点与之前已确认的单元重叠；已保存时间不会被覆盖，请调整打点或明确选择重录。"); }
                    }
                    else
                    {
                        // Cross-row constraints are checked on the completed result below. Within a
                        // row, saved units remain immutable anchors even during partial continuation.
                        if (anchor.LineIndex == target.LineIndex && anchor.Unit.Timing.Start <= start)
                        { throw new ArgumentException("新的打点越过了后续已确认单元；已保存时间不会被覆盖，请调整打点或明确选择重录。"); }
                        // Never silently shorten an explicit Enter end against a later row.
                        // Keep it intact and let row-level validation reject illegal overlaps.
                        bool keepExplicitEnd = explicitLineEnd && target.LineIndex == plan[unitCount - 1].LineIndex
                            && anchor.LineIndex > target.LineIndex;
                        if (!keepExplicitEnd && anchor.Unit.Timing.Start < unitEnd)
                        { unitEnd = anchor.Unit.Timing.Start; }
                    }
                }
                if (unitEnd <= start)
                { throw new ArgumentException("新的单元没有有效时长；请调整入口，不能越过后续已保存的打点。"); }
                units[target.UnitIndex] = units[target.UnitIndex] with
                {
                    Timing = new TimeRange(start, unitEnd),
                    RawInputTime = rawMarkers?[cursor] ?? start,
                    IsConfirmed = true,
                };
                cursor++;
            }
            lines[group.Key] = line with
            {
                Units = units,
                Timing = new TimeRange(units.Min(unit => unit.Timing.Start), units.Max(unit => unit.Timing.End)),
                ReviewState = units.All(LyricRecordingProgress.IsCompleted) ? ReviewState.Confirmed : line.ReviewState,
            };
        }
        return document with { Lines = lines };
    }

    public LyricLine ApplyUnitMarkers(LyricLine line, IReadOnlyList<ProjectTime> markers, IReadOnlyList<ProjectTime>? rawMarkers = null)
    {
        if (line.Units.Count == 0 || markers.Count != line.Units.Count)
        {
            throw new ArgumentException("逐单元打轴需要为每个演唱单元记录一个入口时间。", nameof(markers));
        }

        if (rawMarkers is not null && rawMarkers.Count != markers.Count)
        {
            throw new ArgumentException("原始时间点数量与修正时间点数量不一致。", nameof(rawMarkers));
        }

        for (var index = 0; index < markers.Count; index++)
        {
            if (markers[index] < line.Timing.Start || markers[index] >= line.Timing.End
                || (index > 0 && markers[index] <= markers[index - 1]))
            {
                throw new ArgumentException("单元时间点必须严格递增，且位于当前歌词行的时间范围内。", nameof(markers));
            }
        }

        LyricUnit[] units = line.Units.Select((unit, index) => unit with
        {
            Timing = new TimeRange(markers[index], index + 1 < markers.Count ? markers[index + 1] : line.Timing.End),
            RawInputTime = rawMarkers?[index] ?? markers[index],
            IsConfirmed = true,
        }).ToArray();
        return line with { Units = units, ReviewState = ReviewState.Confirmed };
    }

    public LyricDocument ApplyLineMarkers(
        LyricDocument document,
        IReadOnlyList<ProjectTime> markers,
        ProjectTime songEnd,
        int firstLineIndex = 0)
    {
        if (firstLineIndex < 0 || firstLineIndex >= document.Lines.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(firstLineIndex), "请选择有效的起始歌词行。");
        }

        if (markers.Count == 0 || markers.Count > document.Lines.Count - firstLineIndex)
        {
            throw new ArgumentException("时间点数量必须在待打轴歌词行数以内。", nameof(markers));
        }

        for (var index = 0; index < markers.Count; index++)
        {
            if (markers[index] >= songEnd || (index > 0 && markers[index] <= markers[index - 1]))
            {
                throw new ArgumentException("时间点必须严格递增且位于歌曲结束之前。", nameof(markers));
            }
        }

        LyricLine[] lines = document.Lines.ToArray();
        for (var index = 0; index < markers.Count; index++)
        {
            int lineIndex = firstLineIndex + index;
            ProjectTime end = index + 1 < markers.Count ? markers[index + 1] : songEnd;
            if (index + 1 == markers.Count && lineIndex + 1 < lines.Length
                && lines[lineIndex + 1].Timing.Start > markers[index]
                && lines[lineIndex + 1].Timing.Start < end)
            {
                end = lines[lineIndex + 1].Timing.Start;
            }

            lines[lineIndex] = Retiming(lines[lineIndex], new TimeRange(markers[index], end));
        }

        return document with { Lines = lines };
    }

    public static LyricLine Retiming(LyricLine line, TimeRange timing)
    {
        long oldDuration = line.Timing.Duration.Microseconds;
        LyricUnit[] units = line.Units.Select((unit, index) =>
        {
            long start = oldDuration > 0
                ? Scale(unit.Timing.Start.Microseconds - line.Timing.Start.Microseconds, oldDuration, timing.Duration.Microseconds)
                : timing.Duration.Microseconds * index / line.Units.Count;
            long end = oldDuration > 0
                ? Scale(unit.Timing.End.Microseconds - line.Timing.Start.Microseconds, oldDuration, timing.Duration.Microseconds)
                : timing.Duration.Microseconds * (index + 1) / line.Units.Count;
            return unit with
            {
                Timing = new TimeRange(
                    new ProjectTime(timing.Start.Microseconds + start),
                    new ProjectTime(timing.Start.Microseconds + end)),
                IsConfirmed = false,
            };
        }).ToArray();
        return line with { Timing = timing, Units = units, ReviewState = ReviewState.Unreviewed, ExplicitEndTime = null };
    }

    private static long Scale(long offset, long oldDuration, long newDuration) =>
        (long)((decimal)Math.Clamp(offset, 0, oldDuration) * newDuration / oldDuration);
}
