using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private readonly Func<Window, string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult, MessageBoxResult>
        _lyricSourcePrompt = (owner, message, title, buttons, image, defaultResult) =>
            MessageBox.Show(owner, message, title, buttons, image, defaultResult);

    private LyricLineEditorRow? SourceEditingRow => _lyricRows.FirstOrDefault(row => row.IsSourceEditing);
    private bool HasSourceLyricDraft() => SourceEditingRow is { } row && row.SourceEditText != row.PrimaryText;

    private bool CanLeaveSourceLyricEdit()
    {
        if (!HasSourceLyricDraft()) { return true; }
        StatusText.Text = "歌词原文还有未应用的修改，请先点该行的“应用修改”或“取消”；保存项目时也可一并应用。";
        return false;
    }

    private async Task BeginSourceLyricEditAsync(Guid lineId)
    {
        if (_workspace is null || _isBusy) { return; }
        var row = _lyricRows.FirstOrDefault(item => item.LineId == lineId);
        if (row is null) { return; }
        if (!row.IsSourceEditing)
        {
            if (!CanLeaveSourceLyricEdit() || !CanEditRecordingStructure()) { return; }
            var line = _workspace.Project.Lyrics.Lines.First(item => item.Id == lineId);
            if (!CanEditLanguageContent(line)) { return; }
            if (_playback.IsPlaying) { await _playback.PauseAsync(); PlayPauseButton.Content = "播放"; }
            CancelSourceLyricEdit();
            row.SourceEditText = row.PrimaryText;
            row.IsSourceEditing = true;
        }
        LyricsGrid.SelectedItem = row;
        LyricsGrid.ScrollIntoView(row);
        LyricsGrid.UpdateLayout();
        if (LyricsGrid.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow container
            && FindSourceTextBox(container) is { } input)
        { input.Focus(); input.SelectAll(); }
        StatusText.Text = "正在编辑本行原文：可复制 / 粘贴；Enter 应用，Esc 取消。修改会重建本句单元，其他句不变。";
    }

    private static TextBox? FindSourceTextBox(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBox { Name: "SourceLyricTextBox" } input) { return input; }
            if (FindSourceTextBox(child) is { } nested) { return nested; }
        }
        return null;
    }

    private async void SourceLyricRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow { DataContext: LyricLineEditorRow row }
            || e.ChangedButton != MouseButton.Left || row.IsSourceEditing) { return; }
        e.Handled = true;
        await BeginSourceLyricEditAsync(row.LineId);
    }

    private void SourceLyricRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Bind the menu to the row under the pointer, not a previously selected row.
        if (sender is DataGridRow { DataContext: LyricLineEditorRow row } && !row.IsSourceEditing)
        { LyricsGrid.SelectedItem = row; }
    }

    private async void EditSourceLyric_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LyricLineEditorRow row })
        { await BeginSourceLyricEditAsync(row.LineId); }
    }

    private void CopySourceLyric_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LyricLineEditorRow row }) { return; }
        try
        {
            Clipboard.SetText(row.IsSourceEditing ? row.SourceEditText : row.PrimaryText);
            StatusText.Text = "已复制整行歌词。";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        { StatusText.Text = "剪贴板正被其他程序占用，请稍后重试。"; }
    }

    private async void ApplySourceLyric_Click(object sender, RoutedEventArgs e) =>
        await RunUiOperationAsync(async () => { await ApplySourceLyricDraftAsync(); }, allowSourceDraft: true);

    private void CancelSourceLyric_Click(object sender, RoutedEventArgs e) => CancelSourceLyricEdit();

    private void CancelSourceLyricEdit()
    {
        foreach (var row in _lyricRows.Where(item => item.IsSourceEditing))
        { row.SourceEditText = row.PrimaryText; row.IsSourceEditing = false; }
    }

    private async void SourceLyricTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; CancelSourceLyricEdit(); }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RunUiOperationAsync(async () => { await ApplySourceLyricDraftAsync(); }, allowSourceDraft: true);
        }
        else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        { e.Handled = true; SaveWorkspaceButton_Click(sender, new RoutedEventArgs()); }
        // Native text selection, clipboard and text undo shortcuts remain with the TextBox.
    }

    private async Task<bool> ApplySourceLyricDraftAsync()
    {
        if (_workspace is null || SourceEditingRow is not { } row) { return true; }
        if (!HasSourceLyricDraft()) { CancelSourceLyricEdit(); return true; }
        if (!IsSingleLyricLine(row.SourceEditText))
        { StatusText.Text = "请输入非空的单句歌词；新增句子请使用行前 / 行后插入，不能在一行中粘贴多行。"; return false; }
        if (_markers.Count > 0 || _undoneMarkers.Count > 0 || !CanLeaveQuickEdit())
        { StatusText.Text = "请先处理未应用 / 可重做打点及快速编辑草稿，再修改整句原文。"; return false; }
        int index = _workspace.Project.Lyrics.Lines.ToList().FindIndex(line => line.Id == row.LineId);
        if (index < 0) { return false; }
        var original = _workspace.Project.Lyrics.Lines[index];
        if (!CanEditLanguageContent(original)) { return false; }
        if (original.Units.Any(unit => unit.IsConfirmed || unit.RawInputTime is not null)
            && _lyricSourcePrompt(this, "修改原文会重新生成本句演唱单元，清除本句逐字打点；本句起止时间和其他句子的打轴保持不变。此操作可以撤销。\n\n确定应用修改？",
                "修改已打轴歌词", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        { return false; }
        if (_playback.IsPlaying) { await _playback.PauseAsync(); PlayPauseButton.Content = "播放"; }
        var updated = original with
        {
            PrimaryText = row.SourceEditText, Units = [], LanguageSpans = null, Presentation = null,
            ReadingText = null, RomanizationText = null, ReadingSource = null, ReviewState = ReviewState.Unreviewed,
            AnnotationText = original.AnnotationType == AnnotationType.Translation ? original.AnnotationText : null,
            AnnotationType = original.AnnotationType == AnnotationType.Translation ? AnnotationType.Translation : AnnotationType.None,
        };
        string[] languages = original.LanguageSpans?.Select(span => span.LanguageId).Distinct().ToArray() ?? [];
        string? hint = languages.Length == 1 && languages[0] != LanguageIds.Unknown ? languages[0]
            : LanguageIds.FromLegacy(_workspace.Project.Lyrics.Language);
        var token = _operationCancellation?.Token ?? default;
        updated = await Task.Run(() => _languageAnalysis.AnalyzeLineAsync(updated, hint, cancellationToken: token));
        token.ThrowIfCancellationRequested();
        var lines = _workspace.Project.Lyrics.Lines.ToArray(); lines[index] = updated;
        CommitSourceLyricLines(lines, updated.Id);
        StatusText.Text = "本行原文已更新，可撤销；已重新准备分词 / 读音，本句需重新校准逐字打点，其他句保持不变。";
        return true;
    }

    private async void InsertSourceLyricBefore_Click(object sender, RoutedEventArgs e) => await AskToInsertSourceLyricAsync(sender, true);
    private async void InsertSourceLyricAfter_Click(object sender, RoutedEventArgs e) => await AskToInsertSourceLyricAsync(sender, false);
    private async void AddSourceLyric_Click(object sender, RoutedEventArgs e) => await AskToInsertSourceLyricAsync(null, false);

    private async Task AskToInsertSourceLyricAsync(object? sender, bool before)
    {
        if (!CanLeaveSourceLyricEdit() || !CanEditRecordingStructure()) { return; }
        var row = (sender as FrameworkElement)?.DataContext as LyricLineEditorRow
            ?? LyricsGrid.SelectedItem as LyricLineEditorRow;
        string title = row is null ? "新增歌词行" : $"在第 {row.Number} 行{(before ? "前" : "后")}插入歌词";
        string? text = ShowSourceLyricInsertDialog(title);
        if (text is not null) { await InsertSourceLyricAsync(row?.LineId, before, text); }
    }

    private async Task InsertSourceLyricAsync(Guid? anchorId, bool before, string text)
    {
        if (!CanLeaveSourceLyricEdit() || !CanEditRecordingStructure()) { return; }
        if (!IsSingleLyricLine(text)) { StatusText.Text = "请输入非空的单句歌词，每次插入一行。"; return; }
        var lyrics = _workspace!.Project.Lyrics;
        int anchor = anchorId is { } id ? lyrics.Lines.ToList().FindIndex(line => line.Id == id) : lyrics.Lines.Count - 1;
        if (anchorId is not null && anchor < 0) { return; }
        int insertionIndex = anchor < 0 ? 0 : anchor + (before ? 0 : 1);
        bool reset = LyricLineInsertion.RequiresTimingReset(lyrics.Lines, insertionIndex);
        if (reset && _lyricSourcePrompt(this,
            "当前位置前后两句的时间已相接或重叠，时间轴上没有可插入的空隙。\n\n添加歌词将清除新句子及之后所有歌词的行时间和逐字打点；歌词文字、读音、语言设置保留，前一句的结束时间不变。\n\n确定后打轴页会浏览到新句子，可重新打轴。此操作可以撤销。",
            "插入歌词并清除后续打轴", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        { StatusText.Text = "已取消插入，歌词和打轴未改动。"; return; }
        await RunUiOperationAsync(async () =>
        {
            if (_playback.IsPlaying) { await _playback.PauseAsync(); PlayPauseButton.Content = "播放"; }
            var added = new LyricLine(Guid.NewGuid(), text, AnnotationType.None, null, null,
                new TimeRange(ProjectTime.Zero, ProjectTime.Zero), [], ReviewState.Unreviewed, null);
            added = await Task.Run(() => _languageAnalysis.AnalyzeLineAsync(added,
                LanguageIds.FromLegacy(lyrics.Language), cancellationToken: _operationCancellation!.Token));
            _operationCancellation!.Token.ThrowIfCancellationRequested();
            var lines = LyricLineInsertion.Insert(lyrics.Lines, insertionIndex, added, reset);
            CommitSourceLyricLines(lines, added.Id);
            StatusText.Text = reset
                ? "已插入新句并清除本句及之后的打轴，前一句结束时间保持不变；打轴页已定位到新句，可撤销。"
                : "已插入未打轴的新句，已有句子的时间保持不变；打轴页已定位到新句，可撤销。";
        });
    }

    private void CommitSourceLyricLines(IReadOnlyList<LyricLine> lines, Guid browseId)
    {
        ResetMarkers();
        _loopRange = null; _countdownTarget = null;
        LoopLineButton.Content = "循环选中歌词行";
        CommitRecordingLines(lines, browseId);
        LyricsGrid.SelectedItem = _lyricRows.FirstOrDefault(row => row.LineId == browseId);
        if (LyricsGrid.SelectedItem is { } selected) { LyricsGrid.ScrollIntoView(selected); }
        _lastTapHint = null; RefreshTapHint(_playback.Position);
        RefreshLyricPreview(_playback.Position);
        ProjectTimeline.Update(_workspace!.Project, _playback.Position);
        LyricsGrid.Focus();
    }

    private static bool IsSingleLyricLine(string text) => !string.IsNullOrWhiteSpace(text)
        && text.IndexOfAny(['\r', '\n', '\u2028', '\u2029']) < 0;

    private string? ShowSourceLyricInsertDialog(string title)
    {
        var input = new TextBox { Name = "NewLyricText", MinWidth = 400, FontSize = 16, Padding = new Thickness(8), Margin = new Thickness(0, 12, 0, 8) };
        var error = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap };
        var apply = new Button { Content = "插入", IsDefault = true, Padding = new Thickness(20, 6, 20, 6), Margin = new Thickness(6) };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(20, 6, 20, 6), Margin = new Thickness(6) };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "输入或粘贴一整句歌词。新句需要打轴；若当前位置没有空隙，将另行询问是否清除后续打轴。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(input); panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(apply); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        var dialog = new Window { Owner = this, Title = title, Content = panel, Width = 560, Background = Brushes.White,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        apply.Click += (_, _) =>
        {
            if (!IsSingleLyricLine(input.Text)) { error.Text = "请输入非空的单句歌词，不要粘贴多行。"; return; }
            dialog.DialogResult = true;
        };
        input.Loaded += (_, _) => input.Focus();
        return dialog.ShowDialog() == true ? input.Text : null;
    }
}
