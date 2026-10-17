using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Lyrics;

public sealed record UnitTapTarget(Guid LineId, Guid UnitId, int LineIndex, int UnitIndex, string DisplayText, string Reading,
    string? Romanization = null);

public static class UnitTapPlan
{
    public static IReadOnlyList<UnitTapTarget> Build(LyricDocument document, int firstLineIndex, bool onlyUnconfirmed = false)
    {
        if (firstLineIndex < 0 || firstLineIndex >= document.Lines.Count)
        { throw new ArgumentOutOfRangeException(nameof(firstLineIndex), "请选择有效的起始歌词行。"); }
        return document.Lines.SelectMany((line, lineIndex) => lineIndex < firstLineIndex || line.IsInstrumental || !line.IsHighlightEnabled
            ? Enumerable.Empty<UnitTapTarget>()
            : line.Units.Select((unit, unitIndex) => new UnitTapTarget(line.Id, unit.Id, lineIndex, unitIndex,
                string.Concat(unit.DisplayRanges.Distinct().Select(range => line.PrimaryText.Substring(range.Start, range.Length))), unit.Reading, unit.Romanization))
                .Where(target => !onlyUnconfirmed || !LyricRecordingProgress.IsCompleted(line.Units[target.UnitIndex])))
            .ToArray();
    }
}
