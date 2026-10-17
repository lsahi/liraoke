using System.Windows.Input;
using KaraokeFilmMaker.App.Controls;

namespace KaraokeFilmMaker.App.Tests;

public sealed class EditorShortcutTests
{
    [Fact]
    public void SpacePlaybackAndRecordingDoNotLeakAcrossTabs()
    {
        Assert.Equal(EditorShortcut.TogglePlayback, EditorShortcutPolicy.Resolve(Key.Space, false));
        Assert.Equal(EditorShortcut.Record, EditorShortcutPolicy.Resolve(Key.Space, true));
        Assert.Equal(EditorShortcut.None, EditorShortcutPolicy.Resolve(Key.Enter, false));
        Assert.Equal(EditorShortcut.ApplyMarkers, EditorShortcutPolicy.Resolve(Key.Enter, true));
        Assert.Equal(EditorShortcut.None, EditorShortcutPolicy.Resolve(Key.Back, false));
        Assert.Equal(EditorShortcut.UndoMarker, EditorShortcutPolicy.Resolve(Key.Back, true));
        Assert.Equal(EditorShortcut.TogglePlayback, EditorShortcutPolicy.Resolve(Key.P, true));
        Assert.Equal(EditorShortcut.None, EditorShortcutPolicy.Resolve(Key.Delete, true));
        Assert.Equal(EditorShortcut.DeleteClip, EditorShortcutPolicy.Resolve(Key.Delete, false));
    }

    [Fact]
    public void PreviewTransportUsesJklAndFrameNavigation()
    {
        Assert.Equal(EditorShortcut.SeekBack, EditorShortcutPolicy.Resolve(Key.J, false));
        Assert.Equal(EditorShortcut.Pause, EditorShortcutPolicy.Resolve(Key.K, false));
        Assert.Equal(EditorShortcut.Play, EditorShortcutPolicy.Resolve(Key.L, false));
        Assert.Equal(EditorShortcut.None, EditorShortcutPolicy.Resolve(Key.J, true));
        Assert.Equal(EditorShortcut.PreviousFrame, EditorShortcutPolicy.Resolve(Key.Left, false));
        Assert.Equal(EditorShortcut.NextFrame, EditorShortcutPolicy.Resolve(Key.Right, false));
    }
}
