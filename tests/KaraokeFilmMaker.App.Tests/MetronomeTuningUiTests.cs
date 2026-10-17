using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.App.Tests;

public sealed class MetronomeTuningUiTests
{
    [Fact]
    public Task SubdivisionDraftUpdatesMinorTicksWithoutChangingProject() => Sta(() =>
    {
        using var fixture = new Fixture();
        var guide = fixture.Find<MetronomeBeatPreview>("MetronomeBeatGuide");
        int MinorPixels()
        {
            guide.Measure(new Size(208, 38)); guide.Arrange(new Rect(0, 0, 208, 38));
            var bitmap = new RenderTargetBitmap(208, 38, 96, 96, PixelFormats.Pbgra32); bitmap.Render(guide);
            var pixels = new byte[208 * 38 * 4]; bitmap.CopyPixels(pixels, 208 * 4, 0);
            int count = 0;
            for (int index = 0; index < pixels.Length; index += 4)
            { if (pixels[index] > pixels[index + 2] + 40 && pixels[index + 1] > pixels[index + 2] + 20) { count++; } }
            return count;
        }
        Assert.Equal(0, MinorPixels());
        fixture.Find<ComboBox>("MetronomeSubdivisionCombo").SelectedIndex = 3;
        Assert.Equal(1, fixture.Project.Metronome!.SnapDivisions);
        Assert.Contains("草稿", fixture.Find<TextBlock>("MetronomeDraftText").Text);
        Assert.True(MinorPixels() > 0);
    });

    [Fact]
    public Task WheelsAndTextStayInSyncWithoutChangingSavedSettings() => Sta(() =>
    {
        using var fixture = new Fixture();
        var bpm = fixture.Find<MetronomeValueWheel>("MetronomeBpmWheel");
        var offset = fixture.Find<MetronomeValueWheel>("MetronomeOffsetWheel");
        bpm.Adjust(1, false);
        Assert.Equal(121, fixture.Bpm);
        bpm.Adjust(-1, true);
        Assert.Equal(120.9, fixture.Bpm, 8);
        offset.Adjust(-1, false);
        offset.Adjust(1, true);
        Assert.Equal(-9, fixture.Offset);
        fixture.Find<TextBox>("MetronomeBpmText").Text = 137.5.ToString(CultureInfo.CurrentCulture);
        fixture.Find<TextBox>("MetronomeOffsetText").Text = "123456";
        Assert.Equal(137.5, bpm.Value);
        Assert.Equal(123456, offset.Value);
        Assert.Contains("草稿", fixture.Find<TextBlock>("MetronomeDraftText").Text);
        Assert.Equal(new MetronomeSettings(), fixture.Project.Metronome);
        fixture.Call("RefreshMetronomeFields");
        Assert.Equal(120, bpm.Value);
        Assert.Equal(0, offset.Value);
        Assert.Contains("已同步", fixture.Find<TextBlock>("MetronomeDraftText").Text);
    });

    [Fact]
    public Task DraftWheelsClampAndHalfDoubleKeepSupportedRange() => Sta(() =>
    {
        using var fixture = new Fixture();
        var bpm = fixture.Find<MetronomeValueWheel>("MetronomeBpmWheel");
        var offset = fixture.Find<MetronomeValueWheel>("MetronomeOffsetWheel");
        bpm.Value = 20; bpm.Adjust(-1, true); Assert.Equal(20, bpm.Value);
        bpm.Value = 400; bpm.Adjust(1, false); Assert.Equal(400, bpm.Value);
        offset.Value = -600000; offset.Adjust(-1, false); Assert.Equal(-600000, offset.Value);
        offset.Value = 600000; offset.Adjust(1, true); Assert.Equal(600000, offset.Value);
        fixture.Call("HalveMetronomeBpm_Click", fixture.Window, new RoutedEventArgs());
        Assert.Equal(200, fixture.Bpm);
        fixture.Call("DoubleMetronomeBpm_Click", fixture.Window, new RoutedEventArgs());
        Assert.Equal(400, fixture.Bpm);
        fixture.Call("DoubleMetronomeBpm_Click", fixture.Window, new RoutedEventArgs());
        Assert.Equal(400, fixture.Bpm);
        Assert.Contains("未修改", fixture.Find<TextBlock>("MetronomeDraftText").Text);
    });

    [Fact]
    public Task InvalidTextDoesNotDestroyTheDraftOrReconfigureTheProject() => Sta(() =>
    {
        using var fixture = new Fixture();
        fixture.Find<TextBox>("MetronomeBpmText").Text = "NaN";
        fixture.Find<TextBox>("MetronomeOffsetText").Text = "-";
        Assert.Equal("NaN", fixture.Find<TextBox>("MetronomeBpmText").Text);
        Assert.Equal("-", fixture.Find<TextBox>("MetronomeOffsetText").Text);
        Assert.Equal(120, fixture.Find<MetronomeValueWheel>("MetronomeBpmWheel").Value);
        Assert.Equal(0, fixture.Find<MetronomeValueWheel>("MetronomeOffsetWheel").Value);
        Assert.Contains("检查", fixture.Find<TextBlock>("MetronomeDraftText").Text);
        Assert.Equal(new MetronomeSettings(), fixture.Project.Metronome);
    });

    [Fact]
    public Task MetronomeIsToTheRightOfTheFullHeightMonitorAndScrollsAtMinimumWindowSize() => Sta(() =>
    {
        using var fixture = new Fixture();
        var content = (FrameworkElement)fixture.Window.Content;
        foreach (var size in new[] { new Size(1440, 940), new Size(1100, 760) })
        {
            content.Measure(size); content.Arrange(new Rect(new Point(), size)); content.UpdateLayout();
            var monitor = fixture.Find<ContentControl>("PreviewMonitorHost");
            var panel = fixture.Find<Border>("PreviewSettingsPanel");
            var wheel = fixture.Find<MetronomeValueWheel>("MetronomeBpmWheel");
            var point = wheel.TranslatePoint(new Point(), content);
            Assert.True(point.X > monitor.TranslatePoint(new Point(), content).X + monitor.ActualWidth);
            Assert.InRange(point.Y + wheel.ActualHeight, 0, size.Height);
            Assert.True(Math.Abs(panel.ActualHeight - monitor.ActualHeight) <= 1);
            Assert.Equal(ScrollBarVisibility.Auto, fixture.Find<ScrollViewer>("PreviewSettingsScroll").VerticalScrollBarVisibility);
            var autoButton = fixture.Find<Button>("AutoInitializeMetronomeButton");
            var scroll = fixture.Find<ScrollViewer>("PreviewSettingsScroll");
            Assert.InRange(autoButton.TranslatePoint(new Point(), scroll).Y + autoButton.ActualHeight, 0, scroll.ActualHeight);
            Snapshot(content, $"metronome-tuning-{size.Width}.png", size);
        }
    });

    private sealed class Fixture : IDisposable
    {
        public MainWindow Window { get; } = new();
        public KaraokeProject Project => ((WorkspaceSession)Field("_workspace").GetValue(Window)!).Project;
        public double Bpm => double.Parse(Find<TextBox>("MetronomeBpmText").Text, CultureInfo.CurrentCulture);
        public int Offset => int.Parse(Find<TextBox>("MetronomeOffsetText").Text, CultureInfo.CurrentCulture);
        public Fixture()
        {
            Field("_workspace").SetValue(Window, new WorkspaceSession(Path.GetTempPath(), KaraokeProject.Create("节拍器可视化", DateTimeOffset.UtcNow)));
            Call("RefreshWorkspaceDisplay"); Call("RefreshMetronomeFields");
        }
        public T Find<T>(string name) where T : FrameworkElement => (T)Window.FindName(name);
        public void Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, args);
        public void Dispose() { Field("_workspace").SetValue(Window, null); Field("_allowClose").SetValue(Window, true); Window.Close(); }
    }

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
    private static async Task Sta(Action action)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); complete.TrySetResult(); } catch (Exception e) { complete.TrySetException(e); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await complete.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
