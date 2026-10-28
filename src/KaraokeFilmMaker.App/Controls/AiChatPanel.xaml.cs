using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KaraokeFilmMaker.App.Controls;

public partial class AiChatPanel : UserControl
{
    public event EventHandler? CollapseRequested;
    public event EventHandler? ConfigurationRequested;
    public AiChatPanel()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            bool compact = ActualHeight < 300;
            ChatBorder.Padding = new Thickness(compact ? 8 : 12);
            ChatHeader.Margin = new Thickness(0, 0, 0, compact ? 4 : 10);
            Composer.Margin = new Thickness(0, compact ? 4 : 10, 0, 0);
            SendRow.Margin = new Thickness(0, compact ? 2 : 8, 0, 0);
            MessageInput.Height = compact ? 32 : 76;
        };
    }
    public void FocusInput() => MessageInput.Focus();
    private void Configure_Click(object sender, RoutedEventArgs e) => ConfigurationRequested?.Invoke(this, EventArgs.Empty);
    private void Collapse_Click(object sender, RoutedEventArgs e) => CollapseRequested?.Invoke(this, EventArgs.Empty);
    private void Send_Click(object sender, RoutedEventArgs e) => Send();
    private void MessageInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true; Send();
    }
    private void Send()
    {
        string text = MessageInput.Text.Trim();
        if (text.Length == 0) return;
        Messages.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(237, 247, 252)), CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10), Margin = new Thickness(0, 8, 0, 8),
            Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
        });
        Messages.Children.Add(new TextBlock { Text = "AI暂未接入", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });
        MessageInput.Clear(); ConversationScroll.ScrollToEnd(); MessageInput.Focus();
    }
}
