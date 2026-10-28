using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KaraokeFilmMaker.App.Dialogs;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private Point? _aiDragStart;
    private Point _aiDragOrigin;
    private bool _aiDragged, _aiPositioned;
    private double _aiInspectorWidth = 350;
    private bool _aiInspectorWasEnabled;

    private void InitializeAiUi()
    {
        AssistantPanel.CollapseRequested += (_, _) => CollapseAiChat();
        AssistantPanel.ConfigurationRequested += (_, _) => ShowAiConfiguration();
        Loaded += (_, _) => UpdateAiLayout();
        AiOverlayLayer.SizeChanged += (_, _) => UpdateAiLayout();
        ClientRoot.SizeChanged += (_, _) => UpdateAiLayout();
        PreviewSettingsPanel.SizeChanged += (_, _) => UpdateAiLayout();
        EditorTabs.SizeChanged += (_, _) => UpdateAiLayout();
        EditorTabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == EditorTabs) Dispatcher.BeginInvoke(UpdateAiLayout, DispatcherPriority.Loaded);
        };
    }
    private void AiOpen_Click(object sender, RoutedEventArgs e)
    {
        if (AssistantPanel.Visibility != Visibility.Visible) _aiInspectorWasEnabled = PreviewSettingsPanel.IsEnabled;
        AssistantPanel.Visibility = Visibility.Visible;
        PreviewSettingsPanel.IsEnabled = false;
        UpdateAiLayout(); AssistantPanel.FocusInput();
    }
    private void CollapseAiChat()
    {
        if (AssistantPanel.Visibility != Visibility.Visible) return;
        AssistantPanel.Visibility = Visibility.Collapsed;
        PreviewSettingsPanel.IsEnabled = _aiInspectorWasEnabled;
        AiOpenButton.Focus();
    }
    private void AiPetToggle_Click(object sender, RoutedEventArgs e)
    {
        if (AssistantPanel.Visibility == Visibility.Visible) CollapseAiChat();
        else AiOpen_Click(sender, e);
        AiPet.Greet();
    }
    private void AiConfiguration_Click(object sender, RoutedEventArgs e) => ShowAiConfiguration();
    private void ShowAiConfiguration() => new AiConfigurationWindow(_userPreferenceStore) { Owner = this }.ShowDialog();

    private void UpdateAiLayout()
    {
        if (!IsLoaded || AiOverlayLayer.ActualWidth <= 0 || AiOverlayLayer.ActualHeight <= 0) return;
        Point anchor;
        double height;
        if (PreviewSettingsPanel.IsVisible && PreviewSettingsPanel.ActualWidth > 0)
        {
            _aiInspectorWidth = PreviewSettingsPanel.ActualWidth;
            anchor = PreviewSettingsPanel.TranslatePoint(new Point(), AiOverlayLayer);
            height = PreviewSettingsPanel.ActualHeight;
        }
        else
        {
            Point tabs = EditorTabs.TranslatePoint(new Point(), AiOverlayLayer);
            anchor = new Point(tabs.X + EditorTabs.ActualWidth - _aiInspectorWidth, tabs.Y + 46);
            height = Math.Max(240, EditorTabs.ActualHeight - 46);
        }
        AssistantPanel.Width = Math.Min(_aiInspectorWidth, AiOverlayLayer.ActualWidth - 24);
        AssistantPanel.Height = Math.Max(1, Math.Min(height, AiOverlayLayer.ActualHeight - anchor.Y - 12));
        Canvas.SetLeft(AssistantPanel, Math.Max(12, anchor.X)); Canvas.SetTop(AssistantPanel, Math.Max(0, anchor.Y));
        if (!_aiPositioned)
        {
            PlaceAiButton(AiOverlayLayer.ActualWidth - AiFloatingButton.ActualWidth - 20,
                AiOverlayLayer.ActualHeight - AiFloatingButton.ActualHeight - 16);
            _aiPositioned = true;
        }
        else PlaceAiButton(Canvas.GetLeft(AiFloatingButton), Canvas.GetTop(AiFloatingButton));
    }
    private void PlaceAiButton(double x, double y)
    {
        // A grid's minimum-sized rows can extend beyond the clipped client viewport during resize.
        double visibleWidth = Math.Min(ClientRoot.ActualWidth, AiOverlayLayer.ActualWidth);
        double visibleHeight = Math.Min(ClientRoot.ActualHeight, AiOverlayLayer.ActualHeight);
        Canvas.SetLeft(AiFloatingButton, Math.Clamp(double.IsFinite(x) ? x : 12, 8,
            Math.Max(8, visibleWidth - AiFloatingButton.ActualWidth - 8)));
        Canvas.SetTop(AiFloatingButton, Math.Clamp(double.IsFinite(y) ? y : 12, 8,
            Math.Max(8, visibleHeight - AiFloatingButton.ActualHeight - 8)));
    }
    private void AiFloating_MouseDown(object sender, MouseButtonEventArgs e)
    {
        BeginAiPetDrag(e.GetPosition(AiOverlayLayer));
        AiFloatingButton.CaptureMouse(); e.Handled = true;
    }
    private void AiFloating_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && MoveAiPetDrag(e.GetPosition(AiOverlayLayer))) e.Handled = true;
    }
    private void BeginAiPetDrag(Point position)
    {
        _aiDragStart = position;
        _aiDragOrigin = new Point(Canvas.GetLeft(AiFloatingButton), Canvas.GetTop(AiFloatingButton));
        _aiDragged = false;
    }
    private bool MoveAiPetDrag(Point position)
    {
        if (_aiDragStart is not Point start) return false;
        Vector delta = position - start;
        if (!_aiDragged && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return false;
        if (!_aiDragged) AiPet.SetDragging(true);
        _aiDragged = true; PlaceAiButton(_aiDragOrigin.X + delta.X, _aiDragOrigin.Y + delta.Y); return true;
    }
    private void AiFloating_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_aiDragStart is null) return;
        EndAiPetDrag(cancelled: false); e.Handled = true;
    }
    private void EndAiPetDrag(bool cancelled)
    {
        if (_aiDragStart is null) return;
        bool click = !_aiDragged && !cancelled;
        _aiDragStart = null; _aiDragged = false; AiFloatingButton.ReleaseMouseCapture(); AiPet.SetDragging(false);
        if (click) AiPetToggle_Click(AiFloatingButton, new RoutedEventArgs());
    }
    private void AiFloating_LostCapture(object sender, MouseEventArgs e) => EndAiPetDrag(cancelled: true);
}
