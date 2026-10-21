using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Validation;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Application.Timing;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Japanese;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private readonly List<ProjectTime> _rawMarkers = [];
    private readonly Stack<(ProjectTime Time, ProjectTime Raw)> _undoneMarkers = [];

    private async void TimelineSeekRequested(double seconds)
    {
        if (_playback.IsLoaded && !_isBusy)
        {
            await RunUiOperationAsync(() => _playback.SeekAsync(ProjectTime.FromSeconds(seconds)));
        }
    }

    private async void TimelineEditRequested(TimelineDrag edit)
    {
        if (_workspace is null || _isBusy) { return; }
        await RunUiOperationAsync(() =>
        {
            ApplyLyricRowsToProject(); ApplyVideoRowsToProject();
            if (edit.LineId is null)
            {
                var clip = _workspace.Project.VideoClips.First(item => item.Id == edit.ItemId);
                _workspace = _workspace with
                {
                    Project = _videoTimelineService.UpdateClip(_workspace.Project, clip.Id, edit.Start,
                        edit.SourceStart, edit.Duration, clip.IsMuted, edit.TrackIndex),
                };
                RefreshVideoRows();
            }
            else
            {
                LyricLine[] lines = _workspace.Project.Lyrics.Lines.Select(line => line.Id != edit.LineId ? line : line with
                {
                    Units = line.Units.Select(unit => unit.Id != edit.ItemId ? unit : unit with
                    {
                        Timing = new TimeRange(edit.Start, edit.Start + edit.Duration), IsConfirmed = true,
                    }).ToArray(),
                }).ToArray();
                var updated = _workspace.Project with { Lyrics = _workspace.Project.Lyrics with { Lines = lines } };
                LyricTimingConflicts.Validate(updated.Lyrics);
                IReadOnlyList<string> errors = ProjectValidator.Validate(updated);
                if (errors.Count > 0) { throw new InvalidOperationException(string.Join(Environment.NewLine, errors)); }
                _workspace = _workspace with { Project = updated };
                RefreshLyricRows();
            }
            StatusText.Text = edit.LineId is null ? "视频片段已调整；原声随片段移动，可撤销。" : "歌词时间调整已应用，可撤销。";
            return Task.CompletedTask;
        });
    }

    private void ResetMarkers()
    {
        _markers.Clear(); _rawMarkers.Clear(); _undoneMarkers.Clear();
        _unitTapTargets = null; _unitTapFirstLine = null;
        _rerecordFromSelection = false;
        _lastTapHint = null;
        _recordingEditTarget = null;
        _browsedRecordingLineId = null;
        _markerListDirty = true;
        if (QuickEditPanel is not null) { QuickEditPanel.Visibility = Visibility.Collapsed; }
        if (TapModeCombo is not null) { TapModeCombo.IsEnabled = true; RecordingLineCombo.IsEnabled = true; }
    }

    private bool RecordMarker(ProjectTime raw)
    {
        if (_workspace is null) { return false; }
        if (_browsedRecordingLineId is not null && !CanLeaveQuickEdit()) { return false; }
        if (_unitTapTargets is null && _markers.Count == 0)
        {
            try
            {
                ApplyLyricRowsToProject();
                var progress = RecordingProgress();
                if (TapModeCombo.SelectedIndex == 1 && !_rerecordFromSelection && progress.TotalUnits > 0
                    && progress.ConfirmedUnits == progress.TotalUnits)
                { StatusText.Text = "全部单元已完成。如需修改，请先选择“从浏览句重录”，不会自动覆盖已有打点。"; return false; }
                string? issue = KaraokeFilmMaker.Application.Languages.LanguageAnalysisService.GetRecordingIssue(_workspace.Project.Lyrics, RecordingFirstLineIndex());
                if (issue is not null) { StatusText.Text = issue; NextTapText.Text = issue; return false; }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            { StatusText.Text = exception.Message; return false; }
        }
        if (TapModeCombo.SelectedIndex == 1 && _unitTapTargets is null)
        {
            try
            {
                _unitTapFirstLine = RecordingFirstLineIndex();
                _unitTapTargets = UnitTapPlan.Build(_workspace.Project.Lyrics, _unitTapFirstLine.Value, onlyUnconfirmed: !_rerecordFromSelection);
                if (_unitTapTargets.Count == 0)
                { _unitTapTargets = null; _unitTapFirstLine = null; StatusText.Text = "没有可记录的演唱单元；请检查歌词、间奏及扫色标记。"; return false; }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            { _unitTapFirstLine = null; _unitTapTargets = null; StatusText.Text = exception.Message; return false; }
        }
        if (TapModeCombo.SelectedIndex == 1 && _markers.Count >= _unitTapTargets!.Count + 1)
        { StatusText.Text = "所有字和结束点已录完，请按 Enter 应用，或退格撤销。"; return false; }
        bool endMarker = TapModeCombo.SelectedIndex == 1 && _markers.Count == _unitTapTargets!.Count;
        if (!TryCorrectTapTime(raw, out ProjectTime corrected, endMarker)) { return false; }
        if (_markers.Count > 0 && corrected <= _markers[^1]
            || !endMarker && corrected.Microseconds >= _playback.Duration.Microseconds)
        { StatusText.Text = EffectiveMetronome.Enabled
            ? "新打点吸附到的拍点不晚于上一打点，或已到歌曲末尾。请在后一个拍子继续；也可关闭节拍器自由打轴，已有打点不变。"
            : "入口时间必须递增且在歌曲结束前；请播放歌曲后记录，或先退格撤销。"; return false; }
        if (!endMarker && _unitTapTargets is { } targets
            && (_markers.Count == 0 || targets[_markers.Count - 1].LineId != targets[_markers.Count].LineId))
        {
            // Pending rows will be validated together when applied. Only consult saved rows that
            // this recording session is not replacing, so re-recording never uses stale anchors.
            var pendingLines = targets.Take(_markers.Count).Select(target => target.LineId).ToHashSet();
            var saved = _workspace.Project.Lyrics with
            {
                Lines = _workspace.Project.Lyrics.Lines.Select(line => pendingLines.Contains(line.Id)
                    ? line with { Units = [] } : line).ToArray(),
            };
            if (LyricTimingConflicts.EntryIssue(saved, targets[_markers.Count].LineIndex, corrected) is { } issue)
            { StatusText.Text = issue; return false; }
        }
        _rawMarkers.Add(raw); _markers.Add(corrected); _undoneMarkers.Clear();
        if (_browsedRecordingLineId is not null) { ResumeRecordingFollow(); }
        return true;
    }

    private bool TryCorrectTapTime(ProjectTime raw, out ProjectTime corrected, bool isEndMarker = false)
    {
        corrected = ProjectTime.Zero;
        if (!int.TryParse(InputOffsetText.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int offset)
            || offset is < -2000 or > 2000)
        {
            StatusText.Text = "输入补偿必须是 -2000 到 2000 毫秒之间的整数。";
            return false;
        }
        corrected = new ProjectTime(Math.Clamp(raw.Microseconds + offset * 1000L, 0, _playback.Duration.Microseconds));
        // A song can end between beats. Its real end is a valid terminal boundary,
        // never a new unit entry; otherwise the final word could not be completed.
        if (EffectiveMetronome.Enabled && isEndMarker
            && (raw.Microseconds >= _playback.Duration.Microseconds || corrected.Microseconds == _playback.Duration.Microseconds))
        { corrected = ProjectTime.Zero + _playback.Duration; return true; }
        if (EffectiveMetronome.Enabled && !MetronomeGrid.TrySnap(corrected, EffectiveMetronome, _playback.Duration, out corrected))
        { StatusText.Text = "当前歌曲范围内没有有效拍点，请检查节拍器的 BPM 和偏移。"; return false; }
        return true;
    }

    private void RedoMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_undoneMarkers.TryPop(out var marker))
        {
            _markers.Add(marker.Time); _rawMarkers.Add(marker.Raw); RefreshMarkerList();
        }
    }

    private void UndoMarkerButton_Click(object sender, RoutedEventArgs e) => UndoLastMarker();
    private void UndoLastMarker()
    {
        if (_markers.Count == 0) { return; }
        ProjectTime removed = _markers[^1];
        _undoneMarkers.Push((removed, _rawMarkers[^1]));
        _markers.RemoveAt(_markers.Count - 1); _rawMarkers.RemoveAt(_rawMarkers.Count - 1);
        RefreshMarkerList();
        StatusText.Text = $"已回退一个字 / 时间点 {FormatTime(removed)}，播放未中断。";
    }

    private void MarkerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = MarkerList.SelectedIndex;
        if (index >= 0 && index < _markers.Count) { MarkerTimeText.Text = _markers[index].TotalSeconds.ToString("0.000", CultureInfo.CurrentCulture); }
    }

    private void UpdateMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        int index = MarkerList.SelectedIndex;
        if (index < 0 || !double.TryParse(MarkerTimeText.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double seconds)
            || !double.IsFinite(seconds) || seconds < 0 || seconds >= _playback.Duration.TotalSeconds) { return; }
        _markers[index] = ProjectTime.FromSeconds(seconds);
        _markerListDirty = true;
        RefreshMarkerList();
        StatusText.Text = "时间点已修改；原始敲击时间保持不变。";
    }
}
