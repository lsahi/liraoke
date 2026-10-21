using System.Windows;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using System.Windows.Controls;
using KaraokeFilmMaker.Application.Japanese;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private TimeRange? _loopRange;
    private ProjectTime? _countdownTarget;
    private IReadOnlyList<UnitTapTarget>? _unitTapTargets;
    private int? _unitTapFirstLine;
    private (Guid LineId, Guid UnitId)? _recordingEditTarget;
    private bool _suppressRecordingScroll;
    private (KaraokeProject? Project, int Line, int Mode, int Markers, int Undone)? _lastTapHint;

    private async void PreRollButton_Click(object sender, RoutedEventArgs e)
    {
        if (_markers.Count > 0 || _undoneMarkers.Count > 0)
        { StatusText.Text = "请先应用或清空本次打点，再提前进入；不会自动丢弃未应用或可重做的点。"; return; }
        if (!CanLeaveQuickEdit()) { return; }
        if (_workspace is null || !_playback.IsLoaded || LyricsGrid.SelectedIndex < 0)
        {
            StatusText.Text = "请先选中已有时间的歌词行，并加载歌曲。";
            return;
        }

        bool rerecord = _rerecordFromSelection;
        int selected = RecordingFirstLineIndex();
        await RunUiOperationAsync(async () =>
        {
            ApplyLyricRowsToProject();
            ApplyVideoRowsToProject();
            await PrepareProjectPlaybackAsync();
            LyricLine line = _workspace.Project.Lyrics.Lines[selected];
            var nextUnit = rerecord ? line.Units.FirstOrDefault() : line.Units.FirstOrDefault(unit => !LyricRecordingProgress.IsCompleted(unit));
            ProjectTime target = nextUnit is { Timing.Duration.Microseconds: > 0 } ? nextUnit.Timing.Start
                : line.Timing.Duration.Microseconds > 0 ? line.Timing.Start : _playback.Position;
            ResetMarkers();
            _rerecordFromSelection = rerecord;
            LyricsGrid.SelectedIndex = selected;
            RefreshMarkerList();
            _countdownTarget = target;
            await _playback.SeekAsync(ProjectTime.FromSeconds(Math.Max(0, target.TotalSeconds - 3)));
            await _playback.PlayAsync();
            PlayPauseButton.Content = "暂停";
            StatusText.Text = "已从目标前进入；无行时间时以当前播放头为目标，按空格记录下一个字 / 单词 / 音节。";
        });
    }

    private async void LoopLineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loopRange is not null)
        {
            _loopRange = null;
            LoopLineButton.Content = "循环选中歌词行";
            StatusText.Text = "已关闭区间循环。";
            return;
        }

        if (_workspace is null || !_playback.IsLoaded || LyricsGrid.SelectedIndex < 0)
        {
            StatusText.Text = "请先在打轴页浏览到要循环的歌词句。";
            return;
        }

        int selected = RecordingEditLineIndex();
        await RunUiOperationAsync(async () =>
        {
            ApplyLyricRowsToProject();
            ApplyVideoRowsToProject();
            await PrepareProjectPlaybackAsync();
            TimeRange range = _workspace.Project.Lyrics.Lines[selected].Timing;
            if (range.Duration.Microseconds == 0 || range.End.Microseconds > _playback.Duration.Microseconds)
            {
                throw new InvalidOperationException("循环区间必须有时长且位于歌曲范围内。");
            }

            _loopRange = range;
            LoopLineButton.Content = "关闭区间循环";
            await _playback.SeekAsync(range.Start);
            await _playback.PlayAsync();
            PlayPauseButton.Content = "暂停";
            StatusText.Text = "正在循环选中的歌词行；跨循环的时间点需清空后重新录制。";
        });
    }

    private void RefreshTapHint(ProjectTime position)
    {
        if (_countdownTarget is { } target && position < target)
        {
            NextTapText.Text = $"准备：{Math.Ceiling((target - position).TotalSeconds)} 秒";
            _lastTapHint = null;
            return;
        }
        _countdownTarget = null;
        var key = (_workspace?.Project, LyricsGrid.SelectedIndex, TapModeCombo.SelectedIndex, _markers.Count, _undoneMarkers.Count);
        if (_lastTapHint == key) { return; }
        _lastTapHint = key;
        UpdatePendingTapHighlights();
        RefreshRecordingLanguageWarning(-1);
        RecordingContextText.Text = string.Empty;
        if (_workspace is null || _workspace.Project.Lyrics.Lines.Count == 0)
        {
            RecordingProgressText.Text = "项目尚无已应用打点";
            NextTapText.Text = "请先导入或编写歌词";
            ShowRecordingLines(-1, -1, 0);
            return;
        }
        IReadOnlyList<LyricLine> lines = _workspace.Project.Lyrics.Lines;
        var progress = RecordingProgress();
        RecordingProgressText.Text = $"项目已应用 {progress.ConfirmedUnits}/{progress.TotalUnits} 单元 · {progress.CompletedLines}/{progress.TotalLines} 句完成 · 本次待应用 {_markers.Count} 点";
        int first = Math.Clamp(RecordingFirstLineIndex(), 0, lines.Count - 1);
        if (TapModeCombo.SelectedIndex == 1 && !_rerecordFromSelection && _unitTapTargets is null
            && progress.TotalUnits > 0 && progress.ConfirmedUnits == progress.TotalUnits)
        {
            NextTapText.Text = "全部打轴已完成；可滚轮检查歌词，或选择“从浏览句重录”修改时间。";
            ShowRecordingLines(first, -1, 0);
            RecordingContextText.Text = "绿色为项目中已应用的时间点；重新打开项目后保留。无需从头重打。";
            return;
        }
        if (_unitTapTargets is null && KaraokeFilmMaker.Application.Languages.LanguageAnalysisService.GetRecordingIssue(_workspace.Project.Lyrics, first) is { } issue)
        {
            NextTapText.Text = issue; ShowRecordingLines(first, -1, 0); return;
        }
        int current;
        if (TapModeCombo.SelectedIndex == 1)
        {
            var plan = _unitTapTargets ?? UnitTapPlan.Build(_workspace.Project.Lyrics, first, onlyUnconfirmed: !_rerecordFromSelection);
            if (plan.Count == 0)
            { NextTapText.Text = "没有可演唱的字 / 单词 / 音节；请检查间奏、扫色标记及单元。"; ShowRecordingLines(first, -1, 0); return; }
            int next = Math.Min(_markers.Count, plan.Count - 1);
            UnitTapTarget unit = plan[next];
            current = unit.LineIndex;
            NextTapText.Text = _markers.Count < plan.Count
                ? $"下一单元 · 第 {current + 1} 句 {unit.UnitIndex + 1}/{lines[current].Units.Count}：{unit.Reading}"
                : _markers.Count == plan.Count ? "所有字已记录：再按空格记录尾字结束，或 Enter 结束并应用"
                : "尾字结束点已记录，按 Enter 应用；退格可回退";
            ShowRecordingLines(current, _markers.Count < plan.Count ? unit.UnitIndex : -1,
                _markers.Count < plan.Count ? unit.UnitIndex : lines[current].Units.Count);
            if (_unitTapTargets is not null && LyricsGrid.SelectedIndex != current) { LyricsGrid.SelectedIndex = current; }
        }
        else
        {
            int next = first + _markers.Count;
            current = Math.Min(lines.Count - 1, next);
            ShowRecordingLines(current, -1, 0);
            NextTapText.Text = next < lines.Count
                ? $"辅助逐行模式 · 下一行 {next + 1}：{lines[next].PrimaryText}"
                : "逐行入口录完，按 Enter 应用；卡拉 OK 请使用逐字模式";
        }
        RefreshRecordingLanguageWarning(current);
        RecordingContextText.Text = $"{(_rerecordFromSelection ? "重录模式" : "续录未完成单元")} · 绿色：项目已应用；金色：本次待应用；蓝色：下一单元；橙框：编辑位置。点击可改文字 / 读音。";
    }

    private void TapModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || CurrentRecordingLine is null) { return; }
        _lastTapHint = null; RefreshTapHint(_playback.Position);
    }

    private void ShowRecordingLines(int current, int active, int completed)
    {
        var document = _workspace?.Project.Lyrics;
        int recording = current;
        if (_browsedRecordingLineId is { } browsing && document is not null)
        {
            int index = document.Lines.ToList().FindIndex(line => line.Id == browsing);
            if (index >= 0) { current = index; }
            else { _browsedRecordingLineId = null; }
        }
        bool show = _workspace?.Project.SubtitleStyle.ShowRomanization == true;
        LyricLine? line = document is not null && current >= 0 && current < document.Lines.Count ? document.Lines[current] : null;
        int next = line is not null && current + 1 < document!.Lines.Count ? current + 1 : -1;
        RecordingBrowseText.Text = line is null ? "全部歌词 · 尚未挂载"
            : $"{(_browsedRecordingLineId is null ? "跟随打点" : "浏览中")} · {current + 1}/{document!.Lines.Count} 句 · 滚轮翻看";
        CurrentRecordingLabel.Text = line is null ? "当前句" : $"{(_browsedRecordingLineId is null ? "当前句" : "浏览句")} · {current + 1}" + LanguageSummary(line);
        NextRecordingLabel.Text = next < 0 ? "下一句 · 已到末句" : $"下一句 · {next + 1}";
        CurrentRecordingLabel.Text += RoleSummary(line);
        bool changed = CurrentRecordingLine.LineId != line?.Id;
        CurrentRecordingLine.Update(line, document?.Language ?? LyricLanguage.Chinese, show,
            current == recording ? active : -1, current == recording ? completed : 0, _recordingEditTarget?.UnitId,
            _pendingTapUnitIds, _markers.Count);
        var nextLine = next >= 0 ? document!.Lines[next] : null;
        NextRecordingLabel.Text += RoleSummary(nextLine);
        bool nextChanged = NextRecordingLine.LineId != nextLine?.Id;
        NextRecordingLine.Update(nextLine, document?.Language ?? LyricLanguage.Chinese, show,
            next == recording ? active : -1, next == recording ? completed : 0, _recordingEditTarget?.UnitId,
            _pendingTapUnitIds, _markers.Count);
        if (changed) { CurrentLineScroll.ScrollToLeftEnd(); }
        if (nextChanged) { NextLineScroll.ScrollToLeftEnd(); }
        RefreshExtraRecordingRows(current, recording, active, completed);
        if (changed && _roleSelectedLines.Count == 0 && _roleSelectedUnits.Count == 0) RefreshRoleAssignmentChoices();
        if (!_suppressRecordingScroll && _browsedRecordingLineId is null)
        { Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => CurrentRecordingLine.RevealActive(CurrentLineScroll))); }
    }

    private void RecordingUnitSelected(Guid lineId, Guid unitId)
    {
        if (!CanLeaveQuickEdit()) { return; }
        if (!SelectRoleUnit(lineId, unitId, System.Windows.Input.Keyboard.Modifiers)) ShowQuickEdit();
        _suppressRecordingScroll = true;
        try { _lastTapHint = null; RefreshTapHint(_playback.Position); }
        finally { _suppressRecordingScroll = false; }
        ProjectTimeline.Focus();
    }

    private void ShowQuickEdit()
    {
        QuickEditPanel.Visibility = Visibility.Collapsed;
        if (_roleMultiSelection) return;
        if (_recordingEditTarget is not { } target || _workspace is null) { return; }
        var line = _workspace.Project.Lyrics.Lines.FirstOrDefault(line => line.Id == target.LineId);
        var unit = line?.Units.FirstOrDefault(unit => unit.Id == target.UnitId);
        if (line is null || unit is null) { return; }
        if (!CanEditLanguageContent(line)) { return; }
        try
        {
            var group = LyricUnitEditor.GetGroup(line, unit.Id);
            QuickEditText.Text = group.Text; QuickEditReading.Text = unit.Reading;
            QuickEditHint.Text = group.UnitIndexes.Count > 1
                ? $"原文由 {group.UnitIndexes.Count} 个单元共享；读音只修改选中的「{unit.Reading}」。保持原分组和时间点。"
                : "直接修正文字 / 当前单元读音；编辑时暂停播放，保持原分组和时间点。";
            QuickEditPanel.Visibility = Visibility.Visible;
        }
        catch (InvalidOperationException exception) { StatusText.Text = exception.Message; }
    }

    private async void QuickEdit_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (_playback.IsPlaying) { await _playback.PauseAsync(); PlayPauseButton.Content = "播放"; }
    }

    private void QuickEditClose_Click(object sender, RoutedEventArgs e)
    { QuickEditPanel.Visibility = Visibility.Collapsed; ProjectTimeline.Focus(); }

    private async void QuickEditApply_Click(object sender, RoutedEventArgs e) => await ApplyQuickEditAsync();

    private async Task ApplyQuickEditAsync()
    {
        if (_workspace is null || _recordingEditTarget is not { } target || _isBusy) { return; }
        await RunUiOperationAsync(async () =>
        {
            if (_playback.IsPlaying) { await _playback.PauseAsync(); PlayPauseButton.Content = "播放"; }
            ApplyLyricRowsToProject();
            var lines = _workspace.Project.Lyrics.Lines.ToList();
            int index = lines.FindIndex(line => line.Id == target.LineId);
            if (index < 0) { return; }
            lines[index] = LyricUnitEditor.Edit(lines[index], target.UnitId, QuickEditText.Text,
                QuickEditReading.Text, _workspace.Project.Lyrics.Language);
            lines[index] = _languageAnalysis.RefreshPresentation(lines[index]);
            _workspace = _workspace with { Project = _workspace.Project with { Lyrics = _workspace.Project.Lyrics with { Lines = lines } } };
            RefreshLyricRows(); RebindRecordingPlan(); RefreshMarkerList(); ShowQuickEdit();
            StatusText.Text = "原文 / 读音已修改，已有时间点及未应用打点已保留，可撤销；按 P 继续播放。";
            ProjectTimeline.Focus();
        });
    }

    private void RebindRecordingPlan()
    {
        if (_unitTapTargets is not { Count: > 0 } previous || _workspace is null) { return; }
        int first = _workspace.Project.Lyrics.Lines.ToList().FindIndex(line => line.Units.Any(unit => unit.Id == previous[0].UnitId));
        var updated = first >= 0 ? UnitTapPlan.Build(_workspace.Project.Lyrics, first, onlyUnconfirmed: !_rerecordFromSelection) : [];
        if (!previous.Select(unit => unit.UnitId).SequenceEqual(updated.Select(unit => unit.UnitId)))
        { ResetMarkers(); RefreshMarkerList(); return; }
        _unitTapFirstLine = first; _unitTapTargets = updated;
    }

    private async void SplitRecordingLineButton_Click(object sender, RoutedEventArgs e) => await EditRecordingBoundaryAsync(merge: false);
    private async void MergeRecordingLineButton_Click(object sender, RoutedEventArgs e) => await EditRecordingBoundaryAsync(merge: true);

    private async Task EditRecordingBoundaryAsync(bool merge)
    {
        if (_roleMultiSelection || _workspace is null || _isBusy || !CanLeaveQuickEdit()) { return; }
        await RunUiOperationAsync(async () =>
        {
            ApplyLyricRowsToProject();
            var lines = _workspace.Project.Lyrics.Lines.ToList();
            var target = _recordingEditTarget;
            int index = target is { } chosen ? lines.FindIndex(line => line.Id == chosen.LineId) : RecordingEditLineIndex();
            if (index < 0 || index >= lines.Count) { throw new InvalidOperationException("请先点击要编辑的歌词单元。"); }
            if (_playback.IsPlaying) { await _playback.PauseAsync(); PlayPauseButton.Content = "播放"; }
            if (merge)
            {
                if (index + 1 >= lines.Count) { throw new InvalidOperationException("已经是最后一句，没有下一句可合并。"); }
                lines[index] = LyricLineEditor.Merge(lines[index], lines[index + 1]); lines.RemoveAt(index + 1);
            }
            else
            {
                int unitIndex = target is { } selected ? lines[index].Units.ToList().FindIndex(unit => unit.Id == selected.UnitId)
                    : CurrentRecordingLine.ActiveUnitIndex;
                var split = LyricLineEditor.SplitBeforeUnit(lines[index], unitIndex);
                lines[index] = split.Left; lines.Insert(index + 1, split.Right);
            }
            var lyrics = _workspace.Project.Lyrics with { Lines = lines };
            LyricTimingConflicts.Validate(lyrics);
            _workspace = _workspace with { Project = _workspace.Project with { Lyrics = lyrics } };
            _recordingEditTarget = null;
            QuickEditPanel.Visibility = Visibility.Collapsed;
            if (_browsedRecordingLineId is not null) { _browsedRecordingLineId = lines[index].Id; }
            RefreshLyricRows(); RebindRecordingPlan(); RefreshMarkerList();
            StatusText.Text = merge ? "两句已合并，读音、映射及打点已保留；播放已暂停，可继续录制。"
                : "已在选中单元前换行，读音、映射及打点已保留；播放已暂停，可继续录制。";
            ProjectTimeline.Focus();
        });
    }
}
