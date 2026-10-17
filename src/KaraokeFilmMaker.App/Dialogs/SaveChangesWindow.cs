using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KaraokeFilmMaker.App.Dialogs;

/// <summary>Explicit save/discard/cancel choices; closing the dialog never discards edits.</summary>
public sealed class SaveChangesWindow : Window
{
    public MessageBoxResult Choice { get; private set; } = MessageBoxResult.Cancel;

    public SaveChangesWindow(string message)
    {
        Title = "保存更改";
        Width = 490;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brushes.White;
        Foreground = new SolidColorBrush(Color.FromRgb(52, 66, 77));
        FontFamily = new FontFamily("Segoe UI");
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "关闭前保存更改？", FontSize = 20, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14),
        });
        panel.Children.Add(new TextBlock
        {
            Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14,
            Margin = new Thickness(0, 0, 0, 22),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        AddButton("保存", MessageBoxResult.Yes, isDefault: true);
        AddButton("不保存", MessageBoxResult.No);
        AddButton("取消", MessageBoxResult.Cancel, isCancel: true);
        panel.Children.Add(buttons);
        Content = panel;

        void AddButton(string text, MessageBoxResult choice, bool isDefault = false, bool isCancel = false)
        {
            var button = new Button
            {
                Content = text, MinWidth = 90, Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel,
                Background = new SolidColorBrush(isDefault ? Color.FromRgb(240, 249, 253) : Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromRgb(201, 221, 230)),
                Foreground = new SolidColorBrush(Color.FromRgb(52, 66, 77)),
            };
            button.Click += (_, _) => { Choice = choice; Close(); };
            buttons.Children.Add(button);
        }
    }
}
