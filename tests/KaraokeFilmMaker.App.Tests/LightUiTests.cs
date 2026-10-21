using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;

namespace KaraokeFilmMaker.App.Tests;

public sealed class LightUiTests
{
    [Fact]
    public async Task MainWindowUsesLightChromeWithFourVisibleTabsAndHiddenMetronome() => await Sta(() =>
    {
        var window = new MainWindow();
        try
        {
            var asset = new MediaAsset(Guid.NewGuid(), "夏日的歌.mp4", "media/video/summer.mp4", MediaKind.Video, ProjectDuration.FromSeconds(12));
            var project = KaraokeProject.Create("夏日的歌", DateTimeOffset.UtcNow) with
            {
                MediaAssets = [asset],
                Lyrics = new LyricImportService().Parse("[00:01]把这首歌唱给你听\n[00:05]一起奔向明天", ".lrc", LyricLanguage.Chinese),
                VideoClips = [new VideoClip(Guid.NewGuid(), asset.Id, ProjectTime.Zero, ProjectTime.Zero, ProjectDuration.FromSeconds(12), false)],
            };
            Field("_workspace").SetValue(window, new WorkspaceSession(Path.GetTempPath(), project));
            foreach (string name in new[] { "RefreshWorkspaceDisplay", "RefreshLyricRows", "RefreshVideoRows" }) { Method(name).Invoke(window, null); }
            ((DataGrid)window.FindName("VideoGrid")).SelectedIndex = 0;
            var tabs = (TabControl)window.FindName("EditorTabs");
            Assert.Equal(4, tabs.Items.Cast<TabItem>().Count(tab => tab.Visibility == Visibility.Visible));
            Assert.Null(window.FindName("InspectorTrackText"));
            Assert.Equal(Colors.Black, ((SolidColorBrush)((Border)window.FindName("SharedMonitor")).Background).Color);
            Assert.Equal(Color.FromRgb(250, 252, 253), ((SolidColorBrush)((Grid)window.Content).Background).Color);
            Assert.Contains("StartDisplay", ((System.Windows.Data.Binding)((DataGridTextColumn)((DataGrid)window.FindName("LyricsGrid")).Columns[2]).Binding).Path.Path);
            var content = (FrameworkElement)window.Content;
            foreach (var size in new[] { new Size(1440, 940), new Size(1100, 760) })
            {
                tabs.SelectedItem = window.FindName("PreviewTab");
                Layout(content, size);
                Method("ScrollToPreviewSection").Invoke(window, ["MetronomeSection"]);
                Layout(content, size);
                var metronome = (CheckBox)window.FindName("MetronomeEnabledCheck");
                Assert.Equal(Visibility.Collapsed, ((Border)window.FindName("MetronomeSection")).Visibility);
                Assert.Equal(0, metronome.ActualHeight);
                Snapshot(content, $"light-preview-{size.Width}.png", size);
                tabs.SelectedItem = window.FindName("RecordingTab");
                Layout(content, size);
                Assert.True(((ContentControl)window.FindName("RecordingMonitorHost")).ActualHeight >= 100);
                var loopButton = (Button)window.FindName("LoopLineButton");
                var loopPosition = loopButton.TranslatePoint(new Point(), content);
                Assert.InRange(loopPosition.Y + loopButton.ActualHeight, 0, size.Height);
                Snapshot(content, $"light-recording-{size.Width}.png", size);
                tabs.SelectedItem = window.FindName("LyricSourceTab");
                Layout(content, size); Snapshot(content, $"light-source-{size.Width}.png", size);
            }
        }
        finally { Field("_workspace").SetValue(window, null); Field("_allowClose").SetValue(window, true); window.Close(); }
    });

    [Fact]
    public async Task BothTimelineModesKeepExactlyThreeLanesAndNoSeparateVideoAudioLane() => await Sta(() =>
    {
        var control = new ProjectTimelineControl();
        var project = KaraokeProject.Create("Three lanes", DateTimeOffset.UtcNow);
        foreach (bool units in new[] { false, true })
        {
            control.ShowLyricUnits = units; control.Update(project, ProjectTime.Zero);
            Assert.Equal(26 + 30 + 40 + 40 + 8, control.Height);
            var trackAt = typeof(ProjectTimelineControl).GetMethod("TrackAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Null(trackAt.Invoke(control, [40d])); // Subtitle lane.
            Assert.Equal(0, trackAt.Invoke(control, [80d])); // Only video lane.
            Assert.Null(trackAt.Invoke(control, [130d])); // Song lane, not an extra linked-video lane.
            Assert.Equal(0, control.SelectedTrackIndex);
        }
    });

    private static void Layout(FrameworkElement content, Size size)
    { content.Measure(size); content.Arrange(new Rect(new Point(), size)); content.UpdateLayout(); }
    private static void Snapshot(FrameworkElement content, string name, Size size)
    {
        string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) { return; }
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static async Task Sta(Action action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); finished.TrySetResult(); } catch (Exception error) { finished.TrySetException(error); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
