using System.Windows;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    // Saved confirmation belongs to the project, not the transient undoable tap buffer.
    private LyricDocument? _progressDocument;
    private LyricRecordingProgress? _recordingProgress;
    private bool _rerecordFromSelection;
    private readonly HashSet<Guid> _pendingTapUnitIds = [];
    private IReadOnlyList<UnitTapTarget>? _highlightedTapPlan;
    private int _highlightedTapCount;

    private void UpdatePendingTapHighlights()
    {
        if (!ReferenceEquals(_highlightedTapPlan, _unitTapTargets))
        {
            _pendingTapUnitIds.Clear(); _highlightedTapCount = 0;
            _highlightedTapPlan = _unitTapTargets;
        }
        int count = Math.Min(_markers.Count, _unitTapTargets?.Count ?? 0);
        while (_highlightedTapCount > count)
        { _pendingTapUnitIds.Remove(_unitTapTargets![--_highlightedTapCount].UnitId); }
        while (_highlightedTapCount < count)
        { _pendingTapUnitIds.Add(_unitTapTargets![_highlightedTapCount++].UnitId); }
    }

    private LyricRecordingProgress RecordingProgress()
    {
        var document = _workspace!.Project.Lyrics;
        if (!ReferenceEquals(_progressDocument, document))
        {
            _progressDocument = document;
            _recordingProgress = LyricRecordingProgress.From(document);
        }
        return _recordingProgress!;
    }

    private int RecordingFirstLineIndex() => _rerecordFromSelection || TapModeCombo.SelectedIndex != 1
        ? Math.Max(0, LyricsGrid.SelectedIndex)
        : RecordingProgress().FirstIncompleteLineIndex ?? Math.Max(0, _workspace!.Project.Lyrics.Lines.Count - 1);

    private void RestoreRecordingProgress()
    {
        if (_workspace is null || _unitTapTargets is not null || _markers.Count > 0 || _undoneMarkers.Count > 0) { return; }
        if (!_rerecordFromSelection && TapModeCombo.SelectedIndex == 1 && _lyricRows.Count > 0)
        { LyricsGrid.SelectedIndex = RecordingFirstLineIndex(); }
        _lastTapHint = null;
        RefreshTapHint(_playback.Position);
    }

    private void ContinueUnfinishedRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_markers.Count > 0 || _undoneMarkers.Count > 0)
        { StatusText.Text = "请先应用或清空本次打点，再切回未完成位置；已应用的打点不会被清空。"; return; }
        if (!CanLeaveQuickEdit()) { return; }
        ResetMarkers(); RestoreRecordingProgress(); RefreshMarkerList(); ProjectTimeline.Focus();
        StatusText.Text = "已恢复到未完成位置；续录会保留已应用的打点，音频位置未改变。";
    }
}
