using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Validation;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private bool CanEditRecordingStructure()
    {
        if (_workspace is null || _isBusy || !CanLeaveSourceLyricEdit() || !CanLeaveQuickEdit()) { return false; }
        if (_markers.Count > 0 || _undoneMarkers.Count > 0)
        {
            StatusText.Text = "整句 / 分组 / 增删操作可能改变演唱单元，请先应用或明确清空本次打点。仍可点击单元快捷修正文字和读音，或在单元边界换行/合并。";
            return false;
        }
        return true;
    }

    private void RecordingEditMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        { menu.PlacementTarget = button; menu.IsOpen = true; }
    }

    private async void EditRecordingSentence_Click(object sender, RoutedEventArgs e) => await EditRecordingSentenceAsync(false);
    private async void InsertRecordingSentence_Click(object sender, RoutedEventArgs e) => await EditRecordingSentenceAsync(true);

    private async Task EditRecordingSentenceAsync(bool insert)
    {
        if (!CanEditRecordingStructure()) { return; }
        int index = RecordingEditLineIndex();
        if (!insert && (index < 0 || index >= _workspace!.Project.Lyrics.Lines.Count)) { return; }
        if (!insert && !CanEditLanguageContent(_workspace!.Project.Lyrics.Lines[index])) { return; }
        await RunUiOperationAsync(async () =>
        {
            if (_playback.IsPlaying) { await _playback.PauseAsync(); PlayPauseButton.Content = "播放"; }
            var lines = _workspace!.Project.Lyrics.Lines.ToList();
            var source = insert ? new LyricLine(Guid.NewGuid(), "", AnnotationType.None, null, null,
                new TimeRange(ProjectTime.Zero, ProjectTime.Zero), [], ReviewState.Unreviewed, null) : lines[index];
            var edited = ShowRecordingSentenceEditor(source, insert ? "新增歌词句" : $"编辑第 {index + 1} 句");
            if (edited is null) { return; }
            if (edited.PrimaryText != source.PrimaryText)
            {
                edited = await Task.Run(() => _languageAnalysis.AnalyzeLineAsync(edited,
                    LanguageIds.FromLegacy(_workspace.Project.Lyrics.Language), cancellationToken: _operationCancellation!.Token));
            }
            else { edited = _languageAnalysis.RefreshPresentation(edited); }
            if (insert) { lines.Insert(Math.Clamp(index + 1, 0, lines.Count), edited); }
            else { lines[index] = edited; }
            CommitRecordingLines(lines, edited.Id);
            StatusText.Text = "歌词已更新，可撤销；整句文字 / 假名变更会重新建立该句单元，请重新校准该句打点。其他句保持不变。";
        });
    }

    private void CommitRecordingLines(IReadOnlyList<LyricLine> lines, Guid? browseId)
    {
        var project = _workspace!.Project with { Lyrics = _workspace.Project.Lyrics with { Lines = lines } };
        var errors = ProjectValidator.Validate(project);
        if (errors.Count > 0) { throw new InvalidOperationException(string.Join(Environment.NewLine, errors)); }
        _workspace = _workspace with { Project = project };
        _recordingEditTarget = null; QuickEditPanel.Visibility = Visibility.Collapsed;
        RefreshLyricRows(); _browsedRecordingLineId = browseId; _lastTapHint = null;
        RefreshMarkerList(); ProjectTimeline.Focus();
    }

    private LyricLine? ShowRecordingSentenceEditor(LyricLine source, string title)
    {
        var row = LyricLineEditorRow.FromLine(1, source);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "整句文字或假名变更会重建该句单元，需要重新校准该句打点；小修正优先使用歌词格的快捷编辑。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkGoldenrod, Margin = new Thickness(0, 0, 0, 12) });
        var inputs = new List<TextBox>();
        void TextField(string label, string property)
        {
            panel.Children.Add(new TextBlock { Text = label });
            var input = new TextBox { Padding = new Thickness(6), Margin = new Thickness(0, 3, 0, 9) };
            input.SetBinding(TextBox.TextProperty, new Binding(property) { Source = row, Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true });
            panel.Children.Add(input); inputs.Add(input);
        }
        TextField("歌词原文（单句）", nameof(row.PrimaryText));
        TextField("整句假名 / 拼音（混合语言的读音请用“语言 / 片段”修改）", nameof(row.ReadingText));
        TextField("整句罗马音（空格分隔单元，可留空）", nameof(row.RomanizationText));
        TextField("翻译", nameof(row.TranslationText));
        TextField("开始时间（秒）", nameof(row.StartSeconds));
        TextField("结束时间（秒）", nameof(row.EndSeconds));
        void Choice(string label, string property, object[] choices)
        {
            panel.Children.Add(new TextBlock { Text = label });
            var combo = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", SelectedValuePath = "Value", Margin = new Thickness(0, 3, 0, 9) };
            combo.SetBinding(ComboBox.SelectedValueProperty, new Binding(property) { Source = row, Mode = BindingMode.TwoWay });
            panel.Children.Add(combo);
        }
        Choice("辅助行（中英文）", nameof(row.AnnotationType),
            [new { Name = "无", Value = AnnotationType.None }, new { Name = "假名", Value = AnnotationType.Kana },
                new { Name = "拼音", Value = AnnotationType.Pinyin }, new { Name = "罗马音", Value = AnnotationType.Romaji },
                new { Name = "翻译", Value = AnnotationType.Translation }]);
        Choice("校对状态", nameof(row.ReviewState),
            [new { Name = "待检查", Value = ReviewState.Unreviewed }, new { Name = "稍后处理", Value = ReviewState.ReviewLater },
                new { Name = "已确认", Value = ReviewState.Confirmed }]);
        foreach (var (label, property) in new[] { ("读音已确认", nameof(row.IsReadingConfirmed)), ("启用扫色", nameof(row.IsHighlightEnabled)), ("间奏", nameof(row.IsInstrumental)) })
        {
            var check = new CheckBox { Content = label, Margin = new Thickness(0, 3, 0, 4) };
            check.SetBinding(CheckBox.IsCheckedProperty, new Binding(property) { Source = row, Mode = BindingMode.TwoWay }); panel.Children.Add(check);
        }
        var error = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 4, 12, 4) };
        var apply = new Button { Content = "应用修改", IsDefault = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(6) };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(6) };
        var footer = new StackPanel(); footer.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(apply); buttons.Children.Add(cancel); footer.Children.Add(buttons);
        var root = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var dialog = new Window { Owner = this, Title = title, Content = root, Width = 620, Height = 660,
            MaxHeight = SystemParameters.WorkArea.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        LyricLine? result = null;
        apply.Click += (_, _) =>
        {
            try
            {
                if (inputs.Any(Validation.GetHasError)) { throw new InvalidOperationException("请修正标红字段中的无效数值。"); }
                if (string.IsNullOrWhiteSpace(row.PrimaryText) || row.PrimaryText.IndexOfAny(['\r', '\n']) >= 0)
                { throw new InvalidOperationException("请输入单句歌词；拆分请使用歌词格下方的换行按钮。"); }
                result = row.ToLine(_workspace!.Project.Lyrics.Language);
                dialog.DialogResult = true;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
            { error.Text = exception.Message; }
        };
        return dialog.ShowDialog() == true ? result : null;
    }

    private async void EditRecordingUnits_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditRecordingStructure()) { return; }
        int index = RecordingEditLineIndex(); if (index < 0 || index >= _lyricRows.Count) { return; }
        if (!CanEditLanguageContent(_workspace!.Project.Lyrics.Lines[index])) { return; }
        if (_playback.IsPlaying) { await _playback.PauseAsync(); PlayPauseButton.Content = "播放"; }
        LyricsGrid.SelectedIndex = index; EditLyricUnitsButton_Click(sender, e);
        _lastTapHint = null; RefreshTapHint(_playback.Position);
    }

    private void RegroupRecordingSentence_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditRecordingStructure()) { return; }
        int index = RecordingEditLineIndex(); if (index < 0 || index >= _lyricRows.Count) { return; }
        if (!CanEditLanguageContent(_workspace!.Project.Lyrics.Lines[index])) { return; }
        LyricsGrid.SelectedIndex = index; RegroupLyricsButton_Click(sender, e);
    }

    private async void DeleteRecordingSentence_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditRecordingStructure()) { return; }
        int index = RecordingEditLineIndex();
        if (index < 0 || index >= _workspace!.Project.Lyrics.Lines.Count) { return; }
        if (MessageBox.Show(this, $"删除第 {index + 1} 句及其打点？可撤销。", "删除歌词句",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        await RunUiOperationAsync(async () =>
        {
            if (_playback.IsPlaying) { await _playback.PauseAsync(); PlayPauseButton.Content = "播放"; }
            var lines = _workspace.Project.Lyrics.Lines.ToList(); lines.RemoveAt(index);
            CommitRecordingLines(lines, lines.Count == 0 ? null : lines[Math.Min(index, lines.Count - 1)].Id);
            StatusText.Text = "已删除浏览句，可撤销。";
        });
    }
    private async void MoveRecordingSentenceUp_Click(object sender, RoutedEventArgs e) => await MoveRecordingSentenceAsync(-1);
    private async void MoveRecordingSentenceDown_Click(object sender, RoutedEventArgs e) => await MoveRecordingSentenceAsync(1);
    private async Task MoveRecordingSentenceAsync(int offset)
    {
        if (!CanEditRecordingStructure()) { return; }
        int index = RecordingEditLineIndex(), target = index + offset;
        if (index < 0 || target < 0 || target >= _workspace!.Project.Lyrics.Lines.Count) { return; }
        await RunUiOperationAsync(async () =>
        {
            if (_playback.IsPlaying) { await _playback.PauseAsync(); PlayPauseButton.Content = "播放"; }
            var lines = _workspace.Project.Lyrics.Lines.ToList(); var moving = lines[index]; lines.RemoveAt(index); lines.Insert(target, moving);
            CommitRecordingLines(lines, moving.Id); StatusText.Text = "句序已调整，时间保持原值；已有时间的歌词请检查先后顺序。";
        });
    }
}
