using System.Windows.Input;
using KaraokeFilmMaker.App.Controls;

namespace KaraokeFilmMaker.App.Tests;

public sealed class EditorShortcutTests
{
    [Theory]
    [InlineData(12.3, 10, 15)]
    [InlineData(15, 10, 20)]
    [InlineData(15.000001, 15, 20)]
    [InlineData(14.999999, 10, 15)]
    [InlineData(3, 3, 5)]
    [InlineData(22.4, 20, 22.4)]
    public void ArrowsSeekToAdjacentGridWithinPlayableRange(double position, double previous, double next)
    {
        Assert.Equal(previous, EditorShortcutPolicy.FiveSecondTarget(position, false, 3, 22.4));
        Assert.Equal(next, EditorShortcutPolicy.FiveSecondTarget(position, true, 3, 22.4));
        Assert.Equal(EditorShortcut.PreviousFiveSeconds, EditorShortcutPolicy.Resolve(Key.Left, true));
        Assert.Equal(EditorShortcut.NextFiveSeconds, EditorShortcutPolicy.Resolve(Key.Right, true));
    }
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
    public void PreviewTransportUsesJklAndFiveSecondNavigation()
    {
        Assert.Equal(EditorShortcut.SeekBack, EditorShortcutPolicy.Resolve(Key.J, false));
        Assert.Equal(EditorShortcut.Pause, EditorShortcutPolicy.Resolve(Key.K, false));
        Assert.Equal(EditorShortcut.Play, EditorShortcutPolicy.Resolve(Key.L, false));
        Assert.Equal(EditorShortcut.None, EditorShortcutPolicy.Resolve(Key.J, true));
        Assert.Equal(EditorShortcut.PreviousFiveSeconds, EditorShortcutPolicy.Resolve(Key.Left, false));
        Assert.Equal(EditorShortcut.NextFiveSeconds, EditorShortcutPolicy.Resolve(Key.Right, false));
    }
}
