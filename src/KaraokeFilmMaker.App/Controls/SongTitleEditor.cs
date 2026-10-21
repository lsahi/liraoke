using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Infrastructure.Rendering;

namespace KaraokeFilmMaker.App.Controls;

/// <summary>Independent title drafts, collected by explicit save and recovery.</summary>
public sealed class SongTitleEditor : UserControl
{
    private readonly CheckBox _enabled = new() { Content = "显示曲名" };
    private readonly TextBox _text = new() { MaxLength = 500 };
    private readonly ComboBox _position = new() { ItemsSource = new[] { "左上角", "右上角" } };
    private readonly ComboBox _font = new() { IsEditable = true, ItemsSource = SystemFontCatalog.Names, MaxDropDownHeight = 240 };
    private readonly TextBox _size = new(), _outlineSize = new(), _shadow = new();
    private readonly ColorField _color = new(), _outline = new();
    private KaraokeProject? _project;
    private SongTitleSettings _original = new();
    private string _defaultText = "";
    private bool _syncingFont;
    public event Action? ApplyRequested;

    public SongTitleEditor()
    {
        var panel = new StackPanel { Margin = new Thickness(6) };
        Content = panel; // The inspector owns the single continuous scroll surface.
        panel.Children.Add(_enabled);
        Add("曲名", _text);
        var reset = new Button { Content = "使用歌曲文件名", Margin = new Thickness(0, 4, 0, 4) };
        reset.Click += (_, _) => _text.Text = _defaultText;
        panel.Children.Add(reset);
        Add("位置", _position); Add("字体（可输入搜索）", _font);
        Add("字号（1080p）", _size); Add("文字颜色", _color);
        Add("描边颜色", _outline); Add("描边宽度", _outlineSize); Add("阴影偏移", _shadow);
        var apply = new Button { Content = "应用曲名设置", Margin = new Thickness(0, 10, 0, 6) };
        apply.Click += (_, _) => ApplyRequested?.Invoke();
        panel.Children.Add(apply);
        panel.Children.Add(new TextBlock { Text = "仅作用于曲名，预览与导出同步。修改后请应用或保存工程。", TextWrapping = TextWrapping.Wrap });
        _font.IsTextSearchEnabled = false; _font.StaysOpenOnEdit = true;
        _font.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, e) =>
        {
            if (_syncingFont || e.OriginalSource is not TextBox input) return;
            string text = input.Text; int caret = input.CaretIndex;
            _syncingFont = true;
            try { _font.ItemsSource = SystemFontCatalog.Search(text); _font.Text = text; input.CaretIndex = Math.Min(caret, input.Text.Length); }
            finally { _syncingFont = false; }
        }));
        _font.DropDownOpened += (_, _) =>
        {
            string text = _font.Text; _syncingFont = true;
            try { _font.ItemsSource = SystemFontCatalog.IsInstalled(text) ? SystemFontCatalog.Names : SystemFontCatalog.Search(text); _font.Text = text; }
            finally { _syncingFont = false; }
        };
        Load(null, force: true);
        void Add(string label, UIElement control)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 4) });
            panel.Children.Add(control);
        }
    }

    public void Load(KaraokeProject? project, bool force = false)
    {
        string defaultText = project is null ? "" : new SongTitleSettings().ResolveText(project);
        if (!force && project?.Id == _project?.Id && project?.SongTitle == _original && defaultText == _defaultText) return;
        _project = project; _original = project?.SongTitle ?? new(); _defaultText = defaultText;
        _enabled.IsChecked = _original.Enabled; _text.Text = project is null ? "" : _original.ResolveText(project);
        _position.SelectedIndex = (int)_original.Position;
        _syncingFont = true;
        try { _font.ItemsSource = SystemFontCatalog.Names; _font.Text = _original.FontFamily; }
        finally { _syncingFont = false; }
        _size.Text = _original.FontSize.ToString(CultureInfo.CurrentCulture);
        _color.Text = _original.Color; _outline.Text = _original.OutlineColor;
        _outlineSize.Text = _original.OutlineWidth.ToString(CultureInfo.CurrentCulture);
        _shadow.Text = _original.ShadowOffset.ToString(CultureInfo.CurrentCulture);
        IsEnabled = project is not null;
    }

    public SongTitleSettings Read() => _original with
    {
        Enabled = _enabled.IsChecked == true, Text = _text.Text == _defaultText ? null : _text.Text,
        Position = (SongTitlePosition)Math.Max(0, _position.SelectedIndex), FontFamily = _font.Text.Trim(),
        FontSize = Number(_size.Text), Color = _color.ColorValue!, OutlineColor = _outline.ColorValue!,
        OutlineWidth = Number(_outlineSize.Text), ShadowOffset = Number(_shadow.Text),
    };
    private static double Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) && double.IsFinite(value)
            ? value : throw new InvalidOperationException("曲名字号、描边和阴影必须是有效数值。");
}
