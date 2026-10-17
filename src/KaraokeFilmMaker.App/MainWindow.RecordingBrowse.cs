using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private Guid? _browsedRecordingLineId;
    private int _recordingWheelDelta;

    private int RecordingEditLineIndex()
    {
        if (_workspace is null) { return -1; }
        Guid? id = _recordingEditTarget?.LineId ?? _browsedRecordingLineId ?? CurrentRecordingLine.LineId;
        return id is { } selected ? _workspace.Project.Lyrics.Lines.ToList().FindIndex(line => line.Id == selected)
            : Math.Clamp(LyricsGrid.SelectedIndex, 0, Math.Max(0, _workspace.Project.Lyrics.Lines.Count - 1));
    }

    private bool HasQuickEditDraft()
    {
        if (QuickEditPanel.Visibility != Visibility.Visible || _recordingEditTarget is not { } target || _workspace is null) { return false; }
        var line = _workspace.Project.Lyrics.Lines.FirstOrDefault(line => line.Id == target.LineId);
        var unit = line?.Units.FirstOrDefault(unit => unit.Id == target.UnitId);
        if (unit is null || line is null) { return false; }
        var group = KaraokeFilmMaker.Application.Lyrics.LyricUnitEditor.GetGroup(line, unit.Id);
        return QuickEditText.Text != group.Text || QuickEditReading.Text != unit.Reading;
    }

    private bool CanLeaveQuickEdit()
    {
        if (!HasQuickEditDraft()) { return true; }
        StatusText.Text = "快速编辑还有未应用的修改，请先点“应用修改”，或“收起”放弃草稿后再浏览。";
        return false;
    }

    private void BrowseRecordingLines(int offset)
    {
        if (_workspace is null || _isBusy || _workspace.Project.Lyrics.Lines.Count == 0 || !CanLeaveQuickEdit()) { return; }
        var lines = _workspace.Project.Lyrics.Lines;
        int current = lines.ToList().FindIndex(line => line.Id == (_browsedRecordingLineId ?? CurrentRecordingLine.LineId));
        int next = Math.Clamp(Math.Max(0, current) + offset, 0, lines.Count - 1);
        _browsedRecordingLineId = lines[next].Id;
        _recordingEditTarget = null; QuickEditPanel.Visibility = Visibility.Collapsed;
        _lastTapHint = null; RefreshTapHint(_playback.Position);
    }

    private void RecordingLyrics_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Keep text editing/selection independent of document browsing.
        if (QuickEditPanel.IsKeyboardFocusWithin || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { return; }
        e.Handled = true;
        _recordingWheelDelta += e.Delta;
        int steps = _recordingWheelDelta / Mouse.MouseWheelDeltaForOneLine;
        _recordingWheelDelta %= Mouse.MouseWheelDeltaForOneLine;
        if (steps != 0) { BrowseRecordingLines(-steps); }
    }

    private void BrowsePreviousLine_Click(object sender, RoutedEventArgs e) => BrowseRecordingLines(-1);
    private void BrowseNextLine_Click(object sender, RoutedEventArgs e) => BrowseRecordingLines(1);
    private void ResumeRecordingFollow()
    {
        _browsedRecordingLineId = null; _recordingEditTarget = null;
        QuickEditPanel.Visibility = Visibility.Collapsed; _lastTapHint = null;
    }
    private void FollowRecording_Click(object sender, RoutedEventArgs e)
    {
        if (!CanLeaveQuickEdit()) { return; }
        ResumeRecordingFollow(); RefreshTapHint(_playback.Position); ProjectTimeline.Focus();
    }

    private void RecordFromBrowsedLine_Click(object sender, RoutedEventArgs e)
    {
        if (_markers.Count > 0 || _undoneMarkers.Count > 0)
        { StatusText.Text = "当前有未应用打点或可重做打点。请先应用或明确清空，再切换打轴起点；滚轮浏览不会改动这些记录。"; return; }
        if (!CanLeaveQuickEdit()) { return; }
        int index = RecordingEditLineIndex();
        if (index < 0 || index >= _lyricRows.Count) { return; }
        ResetMarkers(); _rerecordFromSelection = true;
        LyricsGrid.SelectedIndex = index; RefreshMarkerList(); ProjectTimeline.Focus();
        StatusText.Text = $"重录起点已设为第 {index + 1} 句；本次完成并应用的句子会替换原时间，其他句不变。音频位置未改变。";
    }

    private void GoToRecordingLyrics_Click(object sender, RoutedEventArgs e)
    {
        if (!CanLeaveSourceLyricEdit() || !CanLeaveQuickEdit()) { return; }
        var id = (LyricsGrid.SelectedItem as LyricLineEditorRow)?.LineId;
        EditorTabs.SelectedItem = RecordingTab;
        _browsedRecordingLineId = id; _recordingEditTarget = null; QuickEditPanel.Visibility = Visibility.Collapsed;
        _lastTapHint = null; RefreshTapHint(_playback.Position);
    }
}
