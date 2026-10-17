using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Lyrics;

/// <summary>Saved singing-unit progress; imported or estimated timestamps are not recorded taps.</summary>
public sealed record LyricRecordingProgress(int TotalUnits, int ConfirmedUnits, int CompletedLines, int TotalLines,
    int? FirstIncompleteLineIndex)
{
    public bool IsComplete => TotalUnits > 0 && ConfirmedUnits == TotalUnits;

    public static bool IsCompleted(LyricUnit unit) => unit.IsConfirmed && unit.Timing.Duration.Microseconds > 0;

    public static LyricRecordingProgress From(LyricDocument document)
    {
        int totalUnits = 0, confirmedUnits = 0, completedLines = 0, totalLines = 0;
        int? firstIncompleteLineIndex = null;
        for (int index = 0; index < document.Lines.Count; index++)
        {
            var line = document.Lines[index];
            if (line.IsInstrumental || !line.IsHighlightEnabled || line.Units.Count == 0) { continue; }
            int confirmed = line.Units.Count(IsCompleted);
            totalLines++;
            totalUnits += line.Units.Count;
            confirmedUnits += confirmed;
            if (confirmed == line.Units.Count) { completedLines++; }
            else { firstIncompleteLineIndex ??= index; }
        }
        return new(totalUnits, confirmedUnits, completedLines, totalLines, firstIncompleteLineIndex);
    }
}
