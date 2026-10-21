using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.App.Tests;

public sealed class PreviewInspectorLayoutTests
{
    [Theory]
    [InlineData(1920, 1080, 16d / 9)]
    [InlineData(1440, 1080, 4d / 3)]
    [InlineData(1080, 1080, 1)]
    [InlineData(1080, 1920, 1)]
    public async Task PreviewFitsOutputAndAllSectionsShareOneScrollWithoutLosingDrafts(int width, int height, double ratio)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                var project = KaraokeProject.Create("预览布局", DateTimeOffset.UnixEpoch) with { ExportPreset = new(width, height, 24, 4000, 192) };
                Field("_workspace").SetValue(window, new WorkspaceSession(Path.GetTempPath(), project));
                Call(window, "RefreshWorkspaceDisplay");
                Call(window, "RefreshExportPreset");
                var content = (FrameworkElement)window.Content;
                var tabs = (TabControl)window.FindName("PreviewInspectorTabs");
                var scroll = (ScrollViewer)window.FindName("PreviewSettingsScroll");
                var sections = (StackPanel)window.FindName("PreviewSections");
                var monitor = (Border)window.FindName("SharedMonitor");
                var host = (ContentControl)window.FindName("PreviewMonitorHost");
                var canvas = (Grid)window.FindName("PreviewCanvas");
                var bpm = (TextBox)window.FindName("MetronomeBpmText"); bpm.Text = "137";
                foreach (var size in new[] { new Size(1440, 940), new Size(1100, 760) })
                {
                    Layout(); Call(window, "RefreshPreviewMonitorSize"); Layout();
                    Assert.InRange(Math.Abs(monitor.ActualWidth / monitor.ActualHeight - ratio), 0, .015);
                    Assert.True(monitor.ActualWidth <= host.ActualWidth + 1 && monitor.ActualHeight <= host.ActualHeight + 1);
                    Assert.Equal(width, canvas.Width); Assert.Equal(height, canvas.Height);
                    double previousY = -1;
                    Assert.Equal(4, tabs.Items.Cast<TabItem>().Count(tab => tab.Visibility == Visibility.Visible));
                    Assert.Equal(Visibility.Collapsed, ((Border)window.FindName("MetronomeSection")).Visibility);
                    foreach (TabItem tab in tabs.Items.Cast<TabItem>().Where(tab => tab.Visibility == Visibility.Visible))
                    {
                        var section = (Border)window.FindName((string)tab.Tag);
                        double y = section.TranslatePoint(new Point(), sections).Y;
                        Assert.True(y > previousY); previousY = y;
                        tabs.SelectedItem = tab; Call(window, "ScrollToPreviewSection", (string)tab.Tag); Layout();
                        Assert.Same(sections, section.Parent); Assert.True(section.ActualHeight > 30);
                        Assert.InRange(section.TranslatePoint(new Point(), scroll).Y, -1, scroll.ActualHeight - 20);
                        Assert.Equal("137", bpm.Text);
                    }
                    tabs.SelectedIndex = 2; Call(window, "ScrollToPreviewSection", "VisualizationSection"); Layout();
                    Snapshot(content, size, $"preview-{width}x{height}-{size.Width}.png");
                    void Layout() { content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout(); }
                }
                Assert.False(((Button)window.FindName("InspectorMakeAccompanimentButton")).IsEnabled);
                ((TabControl)window.FindName("EditorTabs")).SelectedItem = window.FindName("RecordingTab");
                Assert.True(double.IsNaN(monitor.Width)); Assert.True(double.IsNaN(monitor.Height));
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
            finally { if (window is not null) { Field("_workspace").SetValue(window, null); Field("_allowClose").SetValue(window, true); window.Close(); } }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Call(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static void Snapshot(FrameworkElement content, Size size, string file)
    {
        var root = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR"); if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(root, file)); encoder.Save(stream);
    }
}
