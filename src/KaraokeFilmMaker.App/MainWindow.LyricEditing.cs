using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Application.Japanese;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private async void RegroupLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || LyricsGrid.SelectedItems.Count == 0) { return; }
        Guid[] ids = LyricsGrid.SelectedItems.Cast<LyricLineEditorRow>().Select(row => row.LineId).ToArray();
        if (_markers.Count > 0 || _workspace.Project.Lyrics.Lines.Where(line => ids.Contains(line.Id))
            .SelectMany(line => line.Units).Any(unit => unit.IsConfirmed || unit.RawInputTime is not null))
        {
            if (MessageBox.Show(this, "重新分组会清除选中行原有的逐单元打点和当前未应用的录制点，保留行时间。是否继续？此操作可以撤销。",
                "重新分组", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        }
        await RunUiOperationAsync(async () =>
        {
            ApplyLyricRowsToProject();
            List<LyricLine> lines = [];
            foreach (var line in _workspace.Project.Lyrics.Lines)
            {
                if (!ids.Contains(line.Id)) { lines.Add(line); continue; }
                lines.Add(await Task.Run(() => _languageAnalysis.AnalyzeLineAsync(line,
                    LanguageIds.FromLegacy(_workspace.Project.Lyrics.Language), force: true, cancellationToken: _operationCancellation!.Token)));
            }
            _workspace = _workspace with { Project = _workspace.Project with { Lyrics = _workspace.Project.Lyrics with { Lines = lines } } };
            ResetMarkers(); RefreshMarkerList(); RefreshLyricRows();
            StatusText.Text = "选中行已按片段语言重新分组，人工语言锁定保留；请检查读音与待确认片段后打轴。";
        });
    }

    private void MoveLyricUpButton_Click(object sender, RoutedEventArgs e) => MoveLyric(-1);
    private void MoveLyricDownButton_Click(object sender, RoutedEventArgs e) => MoveLyric(1);

    private void MoveLyric(int offset)
    {
        int index = LyricsGrid.SelectedIndex, target = index + offset;
        if (index >= 0 && target >= 0 && target < _lyricRows.Count)
        {
            LyricsGrid.CommitEdit(DataGridEditingUnit.Cell, true); LyricsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            _lyricRows.Move(index, target); RenumberLyricRows(); LyricsGrid.SelectedIndex = target;
        }
    }

    private async void MergeLyricButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || LyricsGrid.SelectedIndex < 0 || LyricsGrid.SelectedIndex + 1 >= _lyricRows.Count) { return; }
        int index = LyricsGrid.SelectedIndex;
        await RunUiOperationAsync(() =>
        {
            ApplyLyricRowsToProject();
            List<LyricLine> lines = [.. _workspace.Project.Lyrics.Lines];
            lines[index] = LyricLineEditor.Merge(lines[index], lines[index + 1]); lines.RemoveAt(index + 1);
            _workspace = _workspace with { Project = _workspace.Project with { Lyrics = _workspace.Project.Lyrics with { Lines = lines } } };
            RefreshLyricRows(); LyricsGrid.SelectedIndex = index;
            StatusText.Text = "已与下一行合并，原有时间和文字映射已保留。";
            return Task.CompletedTask;
        });
    }

    private async void SplitLyricButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || LyricsGrid.SelectedIndex < 0) { return; }
        string? text = InputDialog("拆分歌词行", "在第几个显示字符之后拆分？拆分后将重新建立单元，并需重新校对注音与翻译。", "1");
        if (!int.TryParse(text, out int count)) { return; }
        int index = LyricsGrid.SelectedIndex;
        await RunUiOperationAsync(() =>
        {
            ApplyLyricRowsToProject();
            List<LyricLine> lines = [.. _workspace.Project.Lyrics.Lines];
            int[] elements = StringInfo.ParseCombiningCharacters(lines[index].PrimaryText);
            if (count <= 0 || count >= elements.Length) { throw new InvalidOperationException("请输入该行字符数以内的有效拆分位置。"); }
            var split = LyricLineEditor.Split(lines[index], elements[count], _workspace.Project.Lyrics.Language);
            lines[index] = split.Left; lines.Insert(index + 1, split.Right);
            _workspace = _workspace with { Project = _workspace.Project with { Lyrics = _workspace.Project.Lyrics with { Lines = lines } } };
            RefreshLyricRows(); LyricsGrid.SelectedIndex = index;
            StatusText.Text = "歌词行已拆分，请校对新的注音、翻译和单元时间；此操作可以撤销。";
            return Task.CompletedTask;
        });
    }

    private async void OffsetLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || LyricsGrid.SelectedItems.Count == 0) { return; }
        string? text = InputDialog("移动选中行时间", "偏移多少毫秒？负数提前，原始敲击时间保持不变。", "0");
        if (!long.TryParse(text, out long milliseconds)) { return; }
        Guid[] ids = LyricsGrid.SelectedItems.Cast<LyricLineEditorRow>().Select(row => row.LineId).ToArray();
        await RunUiOperationAsync(() =>
        {
            ApplyLyricRowsToProject();
            var lines = _workspace.Project.Lyrics.Lines.Select(line => ids.Contains(line.Id)
                ? LyricLineEditor.Offset(line, checked(milliseconds * 1000)) : line).ToArray();
            _workspace = _workspace with { Project = _workspace.Project with { Lyrics = _workspace.Project.Lyrics with { Lines = lines } } };
            RefreshLyricRows();
            StatusText.Text = "选中歌词行及其单元时间已整体偏移。";
            return Task.CompletedTask;
        });
    }

    private string? InputDialog(string title, string prompt, string initial)
    {
        var input = new TextBox { Text = initial, Margin = new Thickness(0, 12, 0, 12), Padding = new Thickness(8) };
        var confirm = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(20, 6, 20, 6), HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap }); panel.Children.Add(input); panel.Children.Add(confirm);
        var dialog = new Window { Owner = this, Title = title, Content = panel, Width = 480, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        input.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }
}
