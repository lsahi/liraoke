using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Lyrics;

/// <summary>Recording rows, not words or language groups, define the overlap boundary.</summary>
public static class LyricTimingConflicts
{
    private static bool Recorded(LyricLine line) => !line.IsInstrumental && line.IsHighlightEnabled
        && line.Units.Count > 0 && line.Units.All(LyricRecordingProgress.IsCompleted);

    public static bool HasExplicitEnd(LyricLine line) => Recorded(line)
        && line.ExplicitEndTime == line.Timing.End;

    public static string? EntryIssue(LyricDocument document, int lineIndex, ProjectTime start)
    {
        for (int previous = 0; previous < lineIndex; previous++)
        {
            var line = document.Lines[previous];
            if (!Recorded(line)) continue;
            if (start <= line.Units[0].Timing.Start)
                return "后一句的入口必须晚于前面句子的入口；句内打点仍须依次递增。";
            var lastUnitEnd = line.Units.Max(unit => unit.Timing.End);
            var end = line.Timing.End > lastUnitEnd ? line.Timing.End : lastUnitEnd;
            if (start >= end) continue;
            if (previous != lineIndex - 1)
                return $"第 {previous + 1} 句尚未结束，不能开始第 {lineIndex + 1} 句；只允许相邻两句重叠。";
            if (!HasExplicitEnd(line))
                return $"第 {previous + 1} 句没有 Enter 明确结束点，请先重录该句并按 Enter 收尾，再录制重叠部分。";
        }
        return null;
    }

    public static void Validate(LyricDocument document)
    {
        for (int index = 0; index < document.Lines.Count; index++)
        {
            var line = document.Lines[index];
            if (!Recorded(line)) continue;
            if (EntryIssue(document, index, line.Units[0].Timing.Start) is { } issue)
                throw new ArgumentException(issue);
        }
    }
}
