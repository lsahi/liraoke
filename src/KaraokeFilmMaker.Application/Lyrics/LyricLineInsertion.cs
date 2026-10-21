using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Lyrics;

/// <summary>Inserts an untimed sentence without silently replacing the user's later timing work.</summary>
public static class LyricLineInsertion
{
    /// <summary>
    /// An insertion index is a gap in document order: zero is before the first line and Count is
    /// after the last. Estimated/imported ranges occupy the timeline too; confirmation flags alone
    /// do not determine whether the gap is available.
    /// </summary>
    public static bool RequiresTimingReset(IReadOnlyList<LyricLine> lines, int insertionIndex)
    {
        ValidateIndex(lines, insertionIndex);
        if (insertionIndex == 0 || insertionIndex == lines.Count) { return false; }

        LyricLine previous = lines[insertionIndex - 1];
        LyricLine next = lines[insertionIndex];
        return previous.Timing.Duration.Microseconds > 0 && next.Timing.Duration.Microseconds > 0
            && previous.Timing.End >= next.Timing.Start;
    }

    /// <summary>
    /// The caller must obtain user confirmation before requesting resetFollowing. All changes are
    /// returned atomically; the source list, its records and any persisted presentation are untouched.
    /// </summary>
    public static IReadOnlyList<LyricLine> Insert(IReadOnlyList<LyricLine> lines, int insertionIndex,
        LyricLine newLine, bool resetFollowing = false)
    {
        ValidateIndex(lines, insertionIndex);
        ArgumentNullException.ThrowIfNull(newLine);
        if (lines.Any(line => line.Id == newLine.Id))
        { throw new ArgumentException("新增歌词必须使用新的行标识，不能直接重复插入已有行。", nameof(newLine)); }
        if (RequiresTimingReset(lines, insertionIndex) && !resetFollowing)
        { throw new InvalidOperationException("当前时间轴上已有歌词，请先确认清除新增句及后续歌词的打轴。前一句的结束时间不会改变。"); }

        LyricLine[] result = new LyricLine[lines.Count + 1];
        for (int index = 0; index < insertionIndex; index++) { result[index] = lines[index]; }
        result[insertionIndex] = ClearTiming(newLine);
        for (int index = insertionIndex; index < lines.Count; index++)
        { result[index + 1] = resetFollowing ? ClearTiming(lines[index]) : lines[index]; }
        return result;
    }

    private static LyricLine ClearTiming(LyricLine line) => line with
    {
        Timing = new TimeRange(ProjectTime.Zero, ProjectTime.Zero),
        ExplicitEndTime = null,
        Units = line.Units.Select(unit => unit with
        {
            Timing = new TimeRange(ProjectTime.Zero, ProjectTime.Zero),
            RawInputTime = null,
            IsConfirmed = false,
        }).ToArray(),
    };

    private static void ValidateIndex(IReadOnlyList<LyricLine> lines, int insertionIndex)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (insertionIndex < 0 || insertionIndex > lines.Count)
        { throw new ArgumentOutOfRangeException(nameof(insertionIndex), "插入位置必须位于歌词行前、行后或两行之间。"); }
    }
}
