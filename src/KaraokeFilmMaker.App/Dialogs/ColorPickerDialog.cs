using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KaraokeFilmMaker.App.Controls;

namespace KaraokeFilmMaker.App.Dialogs;

public sealed class ColorPickerDialog : Window
{
    private readonly Canvas _plane = new() { Height = 190, Focusable = true, ClipToBounds = true, Cursor = Cursors.Cross };
    private readonly Rectangle _white = new() { IsHitTestVisible = false };
    private readonly Rectangle _black = new() { IsHitTestVisible = false };
    private readonly Ellipse _marker = new() { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, Fill = Brushes.Transparent, IsHitTestVisible = false,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 2, ShadowDepth = 0, Color = Colors.Black } };
    private readonly Slider _hue = new() { Minimum = 0, Maximum = 360, SmallChange = 1, LargeChange = 15, Height = 26 };
    private readonly Slider _alpha = new() { Minimum = 0, Maximum = 255, SmallChange = 1, LargeChange = 16, Height = 26 };
    private readonly TextBox _hex = new() { Padding = new Thickness(8), MinWidth = 160 };
    private readonly Border _preview = new() { Width = 66, Height = 34 };
    private readonly TextBlock _message = new() { Margin = new Thickness(0, 6, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly Button _accept = new() { Content = "使用此颜色", IsDefault = true, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Color _initial;
    private double _saturation, _value;
    private bool _syncing;
    public Color SelectedColor { get; private set; }

    public ColorPickerDialog(Color initial)
    {
        _initial = initial;
        Title = "字幕调色盘"; Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(250, 252, 253)); Foreground = new SolidColorBrush(Color.FromRgb(52, 66, 77));
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Liraoke;component/Themes/LightTheme.xaml", UriKind.Relative) });
        var panel = new StackPanel { Margin = new Thickness(20), Background = Background }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "拖动选色，或粘贴颜色编号", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        _white.Fill = new LinearGradientBrush(Colors.White, Colors.Transparent, new Point(0, 0), new Point(1, 0));
        _black.Fill = new LinearGradientBrush(Colors.Transparent, Colors.Black, new Point(0, 0), new Point(0, 1));
        _plane.Children.Add(_white); _plane.Children.Add(_black); _plane.Children.Add(_marker);
        AutomationProperties.SetName(_plane, "饱和度和亮度，方向键微调，Shift 加速");
        panel.Children.Add(_plane);
        panel.Children.Add(Label("色相"));
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        for (int i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1, 255), i / 6d));
        panel.Children.Add(new Border { Height = 6, Background = rainbow, Margin = new Thickness(0, 4, 0, 0) });
        AutomationProperties.SetName(_hue, "色相"); panel.Children.Add(_hue);
        panel.Children.Add(Label("不透明度（0 = 透明，255 = 不透明）"));
        AutomationProperties.SetName(_alpha, "不透明度"); panel.Children.Add(_alpha);
        var presets = new WrapPanel { Margin = new Thickness(0, 8, 0, 12) };
        foreach (string hex in new[] { "#FFFFFF", "#FFD54F", "#24465F", "#000000", "#83CDE8", "#FFE59A", "#34424D", "#FF7777", "#AA88DD", "#77CCA5" })
        {
            HexColor.TryParse(hex, out var color);
            var button = new Button { Width = 32, Height = 28, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(3), ToolTip = hex,
                Content = new Border { Background = new SolidColorBrush(color), Width = 22, Height = 18 } };
            AutomationProperties.SetName(button, "选择颜色 " + hex);
            button.Click += (_, _) => SetColor(Color.FromArgb(SelectedColor.A, color.R, color.G, color.B));
            presets.Children.Add(button);
        }
        panel.Children.Add(presets);
        var exact = new DockPanel();
        var swatch = new Border { Background = HexColor.Checkerboard(), Child = _preview, Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(swatch, Dock.Left); exact.Children.Add(swatch); exact.Children.Add(_hex); panel.Children.Add(exact);
        AutomationProperties.SetName(_hex, "颜色编号");
        _hex.ToolTip = "Ctrl+C 复制 / Ctrl+V 粘贴；#RRGGBB 或 #AARRGGBB，AA 为透明度。";
        panel.Children.Add(_message);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var reset = new Button { Content = "还原", Padding = new Thickness(14, 7, 14, 7) };
        reset.Click += (_, _) => SetColor(_initial); actions.Children.Add(reset);
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0) };
        actions.Children.Add(cancel); actions.Children.Add(_accept); panel.Children.Add(actions);
        _accept.Click += (_, _) => { if (_accept.IsEnabled) DialogResult = true; };
        _plane.SizeChanged += (_, _) => UpdatePlane();
        _plane.MouseLeftButtonDown += (_, e) => { _plane.Focus(); _plane.CaptureMouse(); Pick(e.GetPosition(_plane)); e.Handled = true; };
        _plane.MouseMove += (_, e) => { if (_plane.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Pick(e.GetPosition(_plane)); };
        _plane.MouseLeftButtonUp += (_, e) => { if (_plane.IsMouseCaptured) { _plane.ReleaseMouseCapture(); e.Handled = true; } };
        _plane.KeyDown += (_, e) =>
        {
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? .1 : .01;
            switch (e.Key) { case Key.Left: _saturation -= step; break; case Key.Right: _saturation += step; break;
                case Key.Up: _value += step; break; case Key.Down: _value -= step; break; default: return; }
            _saturation = Math.Clamp(_saturation, 0, 1); _value = Math.Clamp(_value, 0, 1); UpdateColor(); e.Handled = true;
        };
        _hue.ValueChanged += (_, _) => { if (!_syncing) UpdateColor(); };
        _alpha.ValueChanged += (_, _) => { if (!_syncing) UpdateColor(); };
        _hex.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            if (HexColor.TryParse(_hex.Text, out var color)) SetColor(color, preserveText: true);
            else { _accept.IsEnabled = false; _message.Text = "请输入 6 位 RGB 或 8 位 ARGB 色号，可带 #。"; _message.Foreground = Brushes.Firebrick; }
        };
        SetColor(initial);
    }

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 10, 0, 0) };
    private void Pick(Point point)
    {
        if (_plane.ActualWidth <= 0 || _plane.ActualHeight <= 0) return;
        _saturation = Math.Clamp(point.X / _plane.ActualWidth, 0, 1);
        _value = 1 - Math.Clamp(point.Y / _plane.ActualHeight, 0, 1);
        UpdateColor();
    }
    private void SetColor(Color color, bool preserveText = false)
    {
        _syncing = true;
        double max = Math.Max(color.R, Math.Max(color.G, color.B)) / 255d;
        double min = Math.Min(color.R, Math.Min(color.G, color.B)) / 255d, delta = max - min;
        _value = max; _saturation = max == 0 ? 0 : delta / max;
        if (delta > 0)
        {
            double hue = max == color.R / 255d ? (color.G - color.B) / (255d * delta)
                : max == color.G / 255d ? 2 + (color.B - color.R) / (255d * delta) : 4 + (color.R - color.G) / (255d * delta);
            _hue.Value = (hue * 60 + 360) % 360;
        }
        _alpha.Value = color.A;
        _syncing = false;
        Present(color, preserveText);
    }
    private void UpdateColor() => Present(FromHsv(_hue.Value, _saturation, _value, (byte)Math.Round(_alpha.Value)), false);
    private void Present(Color color, bool preserveText)
    {
        SelectedColor = color; _preview.Background = new SolidColorBrush(color);
        _syncing = true;
        try { if (!preserveText) _hex.Text = color.ToString(); }
        finally { _syncing = false; }
        _accept.IsEnabled = true; _message.Foreground = Foreground;
        _message.Text = $"ARGB：{color} · 不透明度 {Math.Round(color.A / 255d * 100)}%";
        UpdatePlane();
    }
    private void UpdatePlane()
    {
        _plane.Background = new SolidColorBrush(FromHsv(_hue.Value, 1, 1, 255));
        _white.Width = _black.Width = _plane.ActualWidth; _white.Height = _black.Height = _plane.ActualHeight;
        Canvas.SetLeft(_marker, Math.Clamp(_saturation * _plane.ActualWidth - 6, 0, Math.Max(0, _plane.ActualWidth - 12)));
        Canvas.SetTop(_marker, Math.Clamp((1 - _value) * _plane.ActualHeight - 6, 0, Math.Max(0, _plane.ActualHeight - 12)));
    }
    public static Color FromHsv(double hue, double saturation, double value, byte alpha)
    {
        double h = ((hue % 360) + 360) % 360 / 60, c = value * saturation, x = c * (1 - Math.Abs(h % 2 - 1)), m = value - c;
        (double r, double g, double b) = h switch { < 1 => (c, x, 0d), < 2 => (x, c, 0d), < 3 => (0d, c, x),
            < 4 => (0d, x, c), < 5 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromArgb(alpha, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
