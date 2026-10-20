using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using KaraokeFilmMaker.App.Dialogs;

namespace KaraokeFilmMaker.App.Controls;

public static class HexColor
{
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        string hex = (text ?? "").Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
            return false;
        if (hex.Length == 6) value |= 0xFF000000;
        color = Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public static Brush Checkerboard()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 12, 12))));
        var grey = new SolidColorBrush(Color.FromRgb(220, 228, 232));
        drawing.Children.Add(new GeometryDrawing(grey, null, new RectangleGeometry(new Rect(0, 0, 6, 6))));
        drawing.Children.Add(new GeometryDrawing(grey, null, new RectangleGeometry(new Rect(6, 6, 6, 6))));
        var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 12, 12) };
        brush.Freeze();
        return brush;
    }
}

/// <summary>A color swatch, exact editable hex value and picker; no project mutation until the host reads it.</summary>
public sealed class ColorField : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(ColorField),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
    public static readonly DependencyProperty AllowEmptyProperty = DependencyProperty.Register(nameof(AllowEmpty), typeof(bool), typeof(ColorField),
        new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty FallbackTextProperty = DependencyProperty.Register(nameof(FallbackText), typeof(string), typeof(ColorField),
        new PropertyMetadata("#FF24465F", Changed));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool AllowEmpty { get => (bool)GetValue(AllowEmptyProperty); set => SetValue(AllowEmptyProperty, value); }
    public string FallbackText { get => (string)GetValue(FallbackTextProperty); set => SetValue(FallbackTextProperty, value); }
    public string? ColorValue => AllowEmpty && string.IsNullOrWhiteSpace(Text) ? null
        : HexColor.TryParse(Text, out var color) ? color.ToString()
        : throw new InvalidOperationException("颜色须为 #RRGGBB 或 #AARRGGBB（AA 为透明度），也可省略 #。");
    public Color? PreviewColor { get; private set; }

    private readonly TextBox _input = new() { Padding = new Thickness(6), VerticalContentAlignment = VerticalAlignment.Center, MinWidth = 96 };
    private readonly Border _swatch = new() { Width = 26, Height = 22 };
    private readonly Button _picker = new() { Padding = new Thickness(5), Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _copy = new() { Content = "复制", Padding = new Thickness(6), Margin = new Thickness(6, 0, 0, 0) };
    private bool _syncing;

    public ColorField()
    {
        var row = new DockPanel();
        _picker.Content = new Border { Background = HexColor.Checkerboard(), Child = _swatch };
        AutomationProperties.SetName(_picker, "打开调色盘");
        AutomationProperties.SetName(_input, "颜色编号，可复制粘贴");
        _input.ToolTip = "#RRGGBB / #AARRGGBB，支持 Ctrl+C / Ctrl+V；AA 为透明度。";
        _copy.ToolTip = "复制当前颜色编号";
        DockPanel.SetDock(_picker, Dock.Left); row.Children.Add(_picker);
        DockPanel.SetDock(_copy, Dock.Right); row.Children.Add(_copy);
        row.Children.Add(_input); Content = row;
        _input.TextChanged += (_, _) => { if (!_syncing) SetCurrentValue(TextProperty, _input.Text); };
        _picker.Click += (_, _) =>
        {
            var dialog = new ColorPickerDialog(PreviewColor ?? Colors.White) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true) SetCurrentValue(TextProperty, dialog.SelectedColor.ToString());
        };
        _copy.Click += (_, _) =>
        {
            if (PreviewColor is not { } color) return;
            try { Clipboard.SetText(color.ToString()); _copy.ToolTip = "已复制 " + color; }
            catch (ExternalException) { _copy.ToolTip = "剪贴板正忙，请重试，或选中色号按 Ctrl+C。"; }
        };
        Refresh();
    }

    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((ColorField)sender).Refresh();
    private void Refresh()
    {
        if (_input is null) return;
        _syncing = true;
        try { if (_input.Text != Text) _input.Text = Text ?? ""; }
        finally { _syncing = false; }
        bool inherited = AllowEmpty && string.IsNullOrWhiteSpace(Text);
        bool valid = HexColor.TryParse(inherited ? FallbackText : Text, out var color);
        PreviewColor = valid ? color : null;
        _swatch.Background = valid ? new SolidColorBrush(color) : Brushes.Transparent;
        _swatch.Child = valid ? null : new TextBlock { Text = "!", Foreground = Brushes.Firebrick, HorizontalAlignment = HorizontalAlignment.Center };
        _input.BorderBrush = valid ? new SolidColorBrush(Color.FromRgb(199, 218, 229)) : Brushes.Firebrick;
        _picker.ToolTip = valid ? $"{(inherited ? "沿用未唱描边：" : "当前颜色：")}{color} · 点击打开调色盘" : "无效色号 · 点击打开调色盘";
        _copy.IsEnabled = valid;
    }
}
