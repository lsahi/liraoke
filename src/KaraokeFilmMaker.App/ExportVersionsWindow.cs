using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KaraokeFilmMaker.App;

public sealed class ExportVersionsWindow : Window
{
    public CheckBox OriginalCheck { get; } = new() { Content = "原唱版本 · -on vocal-", IsChecked = true, Margin = new(0, 12, 0, 12) };
    public CheckBox AccompanimentCheck { get; } = new() { Content = "伴奏版本 · -off vocal-", Margin = new(0, 0, 0, 12) };
    public Button ContinueButton { get; } = new() { Content = "选择保存位置…", MinWidth = 130 };

    public ExportVersionsWindow(bool hasAccompaniment)
    {
        Title = "导出 MP4 · 选择版本"; Width = 440; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(250, 252, 253)); Foreground = new SolidColorBrush(Color.FromRgb(52, 66, 77));
        FontSize = 14;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Liraoke;component/Themes/LightTheme.xaml", UriKind.Relative) });
        var panel = new StackPanel { Margin = new(24), Background = Background };
        panel.Children.Add(new TextBlock { Text = "选择一个或两个版本", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(OriginalCheck); panel.Children.Add(AccompanimentCheck);
        AccompanimentCheck.IsEnabled = hasAccompaniment;
        if (!hasAccompaniment) panel.Children.Add(new TextBlock { Text = "当前歌曲没有可用伴奏，请先制作伴奏。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "两个版本使用相同的画面、字幕和裁切范围。\n预览伴奏开关不影响此处的选择。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 20) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new(10, 0, 0, 0) };
        ContinueButton.Click += (_, _) => DialogResult = true;
        OriginalCheck.Click += (_, _) => UpdateSelection();
        AccompanimentCheck.Click += (_, _) => UpdateSelection();
        buttons.Children.Add(ContinueButton); buttons.Children.Add(cancel); panel.Children.Add(buttons); Content = panel;
    }

    public void UpdateSelection() => ContinueButton.IsEnabled = OriginalCheck.IsChecked == true
        || (AccompanimentCheck.IsEnabled && AccompanimentCheck.IsChecked == true);
}
