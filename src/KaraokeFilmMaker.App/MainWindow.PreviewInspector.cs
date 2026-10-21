using System.Windows;
using System.Windows.Controls;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private void PreviewInspectorTabs_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Clicking an already-selected tab must still jump after manually scrolling elsewhere.
        if (PreviewInspectorTabs.SelectedItem is TabItem { Tag: string name }) ScrollToPreviewSection(name);
    }

    private void PreviewInspectorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != PreviewInspectorTabs || !IsInitialized || PreviewSettingsScroll is null) return;
        if (PreviewInspectorTabs.SelectedItem is TabItem { Tag: string name })
            ScrollToPreviewSection(name);
    }

    private void ScrollToPreviewSection(string name)
    {
        if (FindName(name) is not FrameworkElement section || section.Visibility != Visibility.Visible) return;
        PreviewSettingsScroll.UpdateLayout();
        // Use the content coordinate, not BringIntoView: focusing the navigation must not
        // move the whole editor or hide the preview. Draft controls stay alive when navigating.
        double offset = section.TranslatePoint(new Point(), PreviewSections).Y;
        PreviewSettingsScroll.ScrollToVerticalOffset(Math.Max(0, offset));
    }

    private void PreviewMonitorHost_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshPreviewMonitorSize();

    private void RefreshPreviewMonitorSize()
    {
        if (SharedMonitor is null || PreviewMonitorHost is null) return;
        if (IsRecordingMode)
        {
            SharedMonitor.Width = double.NaN; SharedMonitor.Height = double.NaN;
            return;
        }
        double width = PreviewMonitorHost.ActualWidth, height = PreviewMonitorHost.ActualHeight;
        if (width <= 0 || height <= 0) return;
        // Portrait projects use a square monitor; the internal Viewbox still preserves the
        // actual export canvas ratio, displaying the whole portrait without distortion.
        double ratio = Math.Max(1, PreviewCanvas.Width / PreviewCanvas.Height);
        double fittedWidth = Math.Min(width, height * ratio);
        SharedMonitor.Width = fittedWidth;
        SharedMonitor.Height = fittedWidth / ratio;
    }
}
