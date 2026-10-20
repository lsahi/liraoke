using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private readonly HashSet<Guid> _roleSelectedLines = [], _roleSelectedUnits = [];
    private Guid? _roleLineAnchor, _roleUnitAnchor;
    private bool _roleMultiSelection;
    private int _visibleRecordingRows = 2;
    private LyricDocument? _rowHeightDocument;
    private bool _rowHasSecondary;
    private readonly List<(StackPanel Panel, Button Select, TextBlock Label, ScrollViewer Scroll, RecordingLyricLine Line)> _extraRows = [];

    private void ClearRoleSelection()
    {
        _roleSelectedLines.Clear(); _roleSelectedUnits.Clear();
        _roleLineAnchor = _roleUnitAnchor = null; _roleMultiSelection = false;
        if (RecordingSingleEditActions is not null) RecordingSingleEditActions.IsEnabled = true;
    }

    private bool SelectRoleUnit(Guid lineId, Guid unitId, ModifierKeys modifiers)
    {
        bool multiple = (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        _roleSelectedLines.Clear();
        var units = _workspace?.Project.Lyrics.Lines.SelectMany(l => l.Units).Select(u => u.Id).ToArray() ?? [];
        SelectIds(_roleSelectedUnits, units, unitId, ref _roleUnitAnchor, modifiers);
        _roleMultiSelection = multiple;
        _recordingEditTarget = multiple ? null : (lineId, unitId);
        if (multiple) QuickEditPanel.Visibility = Visibility.Collapsed;
        RecordingSingleEditActions.IsEnabled = !multiple;
        RefreshRoleAssignmentChoices();
        return multiple;
    }

    private void SelectRecordingLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id } || _workspace is null || !CanLeaveQuickEdit()) return;
        SelectRoleLine(id, Keyboard.Modifiers);
    }

    private void SelectRoleLine(Guid id, ModifierKeys modifiers)
    {
        if (_workspace is null) return;
        _roleSelectedUnits.Clear();
        SelectIds(_roleSelectedLines, _workspace.Project.Lyrics.Lines.Select(l => l.Id).ToArray(), id, ref _roleLineAnchor, modifiers);
        _roleMultiSelection = (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        _recordingEditTarget = null; QuickEditPanel.Visibility = Visibility.Collapsed;
        RecordingSingleEditActions.IsEnabled = !_roleMultiSelection;
        RefreshRoleAssignmentChoices();
        _lastTapHint = null; RefreshTapHint(_playback.Position);
    }

    private static void SelectIds(HashSet<Guid> selected, Guid[] order, Guid id, ref Guid? anchor, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Shift) && anchor is { } start && Array.IndexOf(order, start) is >= 0 and var a)
        {
            int b = Array.IndexOf(order, id);
            if (!modifiers.HasFlag(ModifierKeys.Control)) selected.Clear();
            if (b >= 0) foreach (Guid item in order.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1)) selected.Add(item);
        }
        else if (modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!selected.Add(id)) selected.Remove(id);
            anchor = id;
        }
        else { selected.Clear(); selected.Add(id); anchor = id; }
    }

    private void RefreshRoleAssignmentChoices()
    {
        if (RoleAssignmentChoices is null) return;
        var project = _workspace?.Project;
        _roleSelectedLines.RemoveWhere(id => project?.Lyrics.Lines.Any(l => l.Id == id) != true);
        _roleSelectedUnits.RemoveWhere(id => project?.Lyrics.Lines.Any(l => l.Units.Any(u => u.Id == id)) != true);
        var targets = new List<IReadOnlyList<Guid>>();
        if (project is not null)
        {
            foreach (var line in project.Lyrics.Lines)
            {
                if (_roleSelectedLines.Contains(line.Id)) targets.Add(line.RoleIds);
                foreach (var unit in line.Units.Where(u => _roleSelectedUnits.Contains(u.Id))) targets.Add(RoleAssignments.Effective(line, unit));
            }
            if (targets.Count == 0 && project.Lyrics.Lines.FirstOrDefault(l => l.Id == (_browsedRecordingLineId ?? CurrentRecordingLine.LineId)) is { } current)
                targets.Add(current.RoleIds);
        }
        RoleSelectionText.Text = _roleSelectedUnits.Count > 0 ? $"已选 {_roleSelectedUnits.Count} 个演唱单元"
            : _roleSelectedLines.Count > 0 ? $"已选 {_roleSelectedLines.Count} 句" : "作用于当前浏览句";
        RoleAssignmentChoices.Children.Clear();
        foreach (var role in project?.Roles ?? [])
        {
            bool any = targets.Any(ids => ids.Contains(role.Id)), all = targets.Count > 0 && targets.All(ids => ids.Contains(role.Id));
            RoleAssignmentChoices.Children.Add(new CheckBox
            {
                Content = role.Name, Tag = role.Id, IsThreeState = true, IsChecked = all ? true : any ? null : false,
                Margin = new Thickness(0, 4, 10, 4), ToolTip = "实心表示部分选中内容使用此角色；应用时保留各自原有选择。",
            });
        }
    }

    private async void AssignRoles_Click(object sender, RoutedEventArgs e) => await AssignSelectedRolesAsync(false);
    private async void InheritRoles_Click(object sender, RoutedEventArgs e) => await AssignSelectedRolesAsync(true);

    private async Task AssignSelectedRolesAsync(bool inherit)
    {
        if (_workspace is null || !CanLeaveQuickEdit()) return;
        if (inherit && _roleSelectedUnits.Count == 0) { StatusText.Text = "请先选择需要恢复整句角色的演唱单元。"; return; }
        var selectedLines = _roleSelectedLines.ToHashSet();
        if (selectedLines.Count == 0 && _roleSelectedUnits.Count == 0
            && (_browsedRecordingLineId ?? CurrentRecordingLine.LineId) is { } current) selectedLines.Add(current);
        if (selectedLines.Count == 0 && _roleSelectedUnits.Count == 0) return;
        var checks = RoleAssignmentChoices.Children.OfType<CheckBox>().ToDictionary(c => (Guid)c.Tag, c => c.IsChecked);
        await RunUiOperationAsync(() =>
        {
            var project = _workspace.Project;
            // One pass even for a whole-song selection. Indeterminate preserves each target's original value.
            Guid[] Resolve(IReadOnlyList<Guid> previous) => project.Roles
                .Where(r => checks.GetValueOrDefault(r.Id) ?? previous.Contains(r.Id)).Select(r => r.Id).ToArray();
            project = project with { Lyrics = project.Lyrics with { Lines = project.Lyrics.Lines.Select(line =>
                selectedLines.Contains(line.Id)
                    ? line with { RoleIds = Resolve(line.RoleIds), Units = line.Units.Select(u => u with { RoleIds = null }).ToArray() }
                    : line with { Units = line.Units.Select(u => _roleSelectedUnits.Contains(u.Id)
                        ? u with { RoleIds = inherit ? null : Resolve(RoleAssignments.Effective(line, u)) } : u).ToArray() }).ToArray() } };
            CommitRoleProject(project);
            StatusText.Text = "角色分配已应用；歌词和所有已记录、待应用打点保持不变。";
            return Task.CompletedTask;
        });
    }

    private void RecordingLyricsViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Render only the visible page; never build all song cells on every tap.
        int count = RecordingRowCount(e.NewSize.Height);
        if (count == _visibleRecordingRows) return;
        _visibleRecordingRows = count;
        if (!IsInitialized || CurrentRecordingLine is null || _playback is null) return;
        _lastTapHint = null; RefreshTapHint(_playback.Position);
    }

    private void RefreshExtraRecordingRows(int current, int recording, int active, int completed)
    {
        var document = _workspace?.Project.Lyrics;
        _visibleRecordingRows = RecordingRowCount(RecordingLyricsViewport.ActualHeight);
        int extra = Math.Max(0, _visibleRecordingRows - 2);
        while (_extraRows.Count < extra)
        {
            var line = new RecordingLyricLine();
            line.UnitSelected += RecordingUnitSelected;
            var select = new Button { Content = "整句", Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0, 3, 6, 2) };
            select.Click += SelectRecordingLine_Click;
            var label = new TextBlock { FontSize = 11, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 5, 0, 2) };
            var header = new DockPanel(); DockPanel.SetDock(select, Dock.Left); header.Children.Add(select); header.Children.Add(label);
            var scroll = new ScrollViewer { Content = line, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var panel = new StackPanel(); panel.Children.Add(header); panel.Children.Add(scroll);
            AdditionalRecordingRows.Children.Add(panel);
            _extraRows.Add((panel, select, label, scroll, line));
        }
        for (int i = 0; i < _extraRows.Count; i++)
        {
            var row = _extraRows[i]; int index = current + 2 + i;
            var source = document is not null && current >= 0 && index < document.Lines.Count ? document.Lines[index] : null;
            row.Panel.Visibility = i < extra && source is not null ? Visibility.Visible : Visibility.Collapsed;
            if (row.Panel.Visibility != Visibility.Visible) continue;
            bool changed = row.Line.LineId != source!.Id;
            row.Line.Update(source, document!.Language, _workspace!.Project.SubtitleStyle.ShowRomanization,
                index == recording ? active : -1, index == recording ? completed : 0, _recordingEditTarget?.UnitId, _pendingTapUnitIds, _markers.Count);
            row.Label.Text = $"第 {index + 1} 句" + RoleSummary(source);
            UpdateRoleSelectionVisual(row.Line, row.Select, source);
            if (changed) row.Scroll.ScrollToLeftEnd();
        }
        UpdateRoleSelectionVisual(CurrentRecordingLine, SelectCurrentLineButton,
            document?.Lines.FirstOrDefault(l => l.Id == CurrentRecordingLine.LineId));
        UpdateRoleSelectionVisual(NextRecordingLine, SelectNextLineButton,
            document?.Lines.FirstOrDefault(l => l.Id == NextRecordingLine.LineId));
    }

    private int RecordingRowCount(double height)
    {
        var document = _workspace?.Project.Lyrics;
        if (!ReferenceEquals(document, _rowHeightDocument))
        {
            _rowHeightDocument = document;
            _rowHasSecondary = document?.Language == LyricLanguage.Japanese || document?.Lines.Any(l =>
                l.Presentation?.RecordingCells.Any(c => !string.IsNullOrEmpty(c.SecondaryText)) == true) == true;
        }
        double rowHeight = _rowHasSecondary ? _workspace?.Project.SubtitleStyle.ShowRomanization == true ? 104 : 90 : 62;
        return Math.Clamp((int)(height / rowHeight), 2, 30);
    }

    private string RoleSummary(LyricLine? line)
    {
        if (line is null || _workspace is null) return "";
        var names = _workspace.Project.Roles.Where(r => line.RoleIds.Contains(r.Id)).Select(r => r.Name);
        return " · " + (names.Any() ? string.Join(" + ", names) : "标准")
            + (line.Units.Any(u => u.RoleIds is not null) ? "（含单元角色）" : "");
    }

    private void UpdateRoleSelectionVisual(RecordingLyricLine control, Button button, LyricLine? line)
    {
        button.Tag = line?.Id; button.IsEnabled = line is not null;
        bool selected = line is not null && _roleSelectedLines.Contains(line.Id);
        button.Background = selected ? Brushes.LightGoldenrodYellow : Brushes.White;
        control.SetRoleSelection(_roleSelectedUnits, selected);
        control.SetRoleAppearance(_workspace?.Project.Roles ?? []);
    }
}
