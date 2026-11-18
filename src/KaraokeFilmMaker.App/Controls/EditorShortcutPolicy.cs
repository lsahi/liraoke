using System.Windows.Input;

namespace KaraokeFilmMaker.App.Controls;

public enum EditorShortcut { None, TogglePlayback, Pause, Play, SeekBack, PreviousFiveSeconds, NextFiveSeconds, Start, End, Record, UndoMarker, ApplyMarkers, DeleteClip }

public static class EditorShortcutPolicy
{
    public static double FiveSecondTarget(double position, bool forward, double start, double end)
    {
        double gridIndex = forward ? Math.Floor(position / 5) + 1 : Math.Ceiling(position / 5) - 1;
        return Math.Clamp(gridIndex * 5, start, end);
    }

    public static EditorShortcut Resolve(Key key, bool recording) => key switch
    {
        Key.Space => recording ? EditorShortcut.Record : EditorShortcut.TogglePlayback,
        Key.P when recording => EditorShortcut.TogglePlayback,
        Key.Back when recording => EditorShortcut.UndoMarker,
        Key.Enter when recording => EditorShortcut.ApplyMarkers,
        Key.J when !recording => EditorShortcut.SeekBack,
        Key.K when !recording => EditorShortcut.Pause,
        Key.L when !recording => EditorShortcut.Play,
        Key.Left => EditorShortcut.PreviousFiveSeconds,
        Key.Right => EditorShortcut.NextFiveSeconds,
        Key.Home => EditorShortcut.Start,
        Key.End => EditorShortcut.End,
        Key.Delete when !recording => EditorShortcut.DeleteClip,
        _ => EditorShortcut.None,
    };
}
