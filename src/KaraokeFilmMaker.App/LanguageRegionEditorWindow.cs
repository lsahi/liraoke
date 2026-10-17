using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.App;

public sealed record LanguageChoice(string Id, string Name, bool SupportsManualReading = false);
public sealed record LanguageRegionSummary(TextRange Range, string Text, string Language, string Status, string? Reading);
public sealed record LanguageRegionEdit(TextRange Range, string? LanguageId, string? Reading);

/// <summary>Collects one explicit region edit. The owner applies it transactionally after warning about timing changes.</summary>
public sealed class LanguageRegionEditorWindow : Window
{
    private readonly TextBox _text;
    private readonly ComboBox _language;
    private readonly TextBox _reading;
    private readonly TextBlock _selection;
    private readonly TextBlock _error;
    private readonly string _original;

    public LanguageRegionEditorWindow(string text, IReadOnlyList<LanguageChoice> languages,
        IReadOnlyList<LanguageRegionSummary> regions, TextRange? initialSelection = null)
    {
        _original = text;
        NameScope.SetNameScope(this, new NameScope());
        Title = "语言与演唱片段"; Width = 760; Height = 610; Background = Brushes.White;
        MaxHeight = SystemParameters.WorkArea.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(18), Background = Brushes.White };
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        _error = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
        footer.Children.Add(_error);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var redetect = new Button { Content = "重新自动识别选区", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(4) };
        var apply = new Button { Content = "应用语言 / 读音并锁定", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(4), IsDefault = true };
        var cancel = new Button { Content = "取消", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(4), IsCancel = true };
        buttons.Children.Add(redetect); buttons.Children.Add(apply); buttons.Children.Add(cancel); footer.Children.Add(buttons);
        var panel = new StackPanel(); root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock
        {
            Text = "选中原文中的一个片段，指定其语言和可选读音。自动识别不联网；不确定的片段需要校对。人工指定将锁定，重新自动识别才会解除该选区的锁定。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        _text = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 22,
            Padding = new Thickness(10), MinHeight = 75, IsReadOnlyCaretVisible = true };
        RegisterName("RegionText", _text); panel.Children.Add(_text);
        var selectAll = new Button { Content = "选择整句", HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 6, 0, 6) };
        selectAll.Click += (_, _) => { _text.Focus(); _text.SelectAll(); }; panel.Children.Add(selectAll);
        _selection = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) }; panel.Children.Add(_selection);
        panel.Children.Add(new TextBlock { Text = "选区演唱语言" });
        _language = new ComboBox { ItemsSource = languages, DisplayMemberPath = nameof(LanguageChoice.Name),
            SelectedValuePath = nameof(LanguageChoice.Id), SelectedIndex = languages.Count > 0 ? 0 : -1, Margin = new Thickness(0, 4, 0, 8) };
        RegisterName("RegionLanguage", _language); panel.Children.Add(_language);
        panel.Children.Add(new TextBlock { Text = "选区读音（插件支持时可填；日语填写假名，其他情况可用“单元精修”修改读音）", TextWrapping = TextWrapping.Wrap });
        _reading = new TextBox { Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(6) };
        RegisterName("RegionReading", _reading); panel.Children.Add(_reading);
        _reading.IsEnabled = (_language.SelectedItem as LanguageChoice)?.SupportsManualReading == true;
        _language.SelectionChanged += (_, _) =>
        {
            _reading.Clear(); _reading.IsEnabled = (_language.SelectedItem as LanguageChoice)?.SupportsManualReading == true;
        };
        panel.Children.Add(new TextBlock { Text = "当前识别结果（点击一行可选中对应原文）", Margin = new Thickness(0, 4, 0, 4) });
        var list = new ListBox { ItemsSource = regions, MaxHeight = 160 };
        var template = new DataTemplate(typeof(LanguageRegionSummary));
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new RegionSummaryConverter() });
        label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); label.SetValue(TextBlock.MarginProperty, new Thickness(4));
        template.VisualTree = label; list.ItemTemplate = template; panel.Children.Add(list);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not LanguageRegionSummary region) { return; }
            _text.Select(region.Range.Start, region.Range.Length); _language.SelectedValue = region.Language;
            _reading.Text = _reading.IsEnabled ? region.Reading ?? "" : "";
        };
        _text.SelectionChanged += (_, _) =>
        {
            _selection.Text = _text.SelectionLength > 0 ? $"选区：{_text.SelectedText}" : "请先用鼠标选择要处理的文字。";
            // A reading belongs to a particular range, not to the next selection.
            _reading.Clear();
            var covering = regions.FirstOrDefault(region => region.Range.Start <= _text.SelectionStart
                && region.Range.EndExclusive >= _text.SelectionStart + _text.SelectionLength);
            if (covering is not null) { _language.SelectedValue = covering.Language; }
        };
        apply.Click += (_, _) => PrepareEdit(false);
        redetect.Click += (_, _) => PrepareEdit(true);
        Content = root;
        if (initialSelection is { } range && range.EndExclusive <= text.Length) { _text.Select(range.Start, range.Length); }
        else { _selection.Text = "请先用鼠标选择要处理的文字。"; }
    }

    public LanguageRegionEdit? RequestedEdit { get; private set; }

    private void PrepareEdit(bool automatic)
    {
        if (!TryCreateEdit(automatic, out var edit, out string error)) { _error.Text = error; return; }
        RequestedEdit = edit; DialogResult = true;
    }

    public bool TryCreateEdit(bool automatic, out LanguageRegionEdit? edit, out string error)
    {
        edit = null; error = "";
        int start = _text.SelectionStart, end = start + _text.SelectionLength;
        var boundaries = StringInfo.ParseCombiningCharacters(_original).Append(_original.Length).ToHashSet();
        if (end <= start || !boundaries.Contains(start) || !boundaries.Contains(end))
        { error = "请选择完整文字片段；不能切开组合字符。"; return false; }
        string? language = automatic ? null : (_language.SelectedItem as LanguageChoice)?.Id;
        if (!automatic && language is null) { error = "请选择已加载的语言。"; return false; }
        string? reading = automatic || !_reading.IsEnabled || string.IsNullOrWhiteSpace(_reading.Text) ? null : _reading.Text.Trim();
        if (reading?.IndexOfAny(['\r', '\n']) >= 0) { error = "片段读音不能换行。"; return false; }
        edit = new(new TextRange(start, end - start), language, reading); return true;
    }

    private sealed class RegionSummaryConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is LanguageRegionSummary region ? $"{region.Text}  ·  {region.Language}  ·  {region.Status}" : "";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
