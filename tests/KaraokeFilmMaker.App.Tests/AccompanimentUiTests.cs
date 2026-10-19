using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.App.Tests;

public class AccompanimentUiTests
{
    [Fact]
    public Task ToolbarInspectorMappingAndTimingIsolation() => Sta(() =>
    {
        string root = Path.Combine(Path.GetTempPath(), "Liraoke.AccompanimentUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "accompaniments"));
        File.WriteAllBytes(Path.Combine(root, "accompaniments", "song.wav"), new byte[100]);
        var window = new MainWindow();
        try
        {
            var make = (Button)window.FindName("MakeAccompanimentButton");
            var toolbar = Assert.IsType<WrapPanel>(make.Parent);
            Assert.Equal(new[] { "新建工作区", "打开工作区", "制作伴奏", "撤销", "保存", "导出 MP4", "取消任务" },
                toolbar.Children.OfType<Button>().Where(b => b.Visibility == Visibility.Visible).Select(b => b.Content.ToString()));
            var toggle = (CheckBox)window.FindName("AccompanimentPreviewCheck"); Assert.False(toggle.IsEnabled);
            var asset = new MediaAsset(Guid.NewGuid(), "测试歌曲", "song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(10));
            var project = KaraokeProject.Create("伴奏预览", DateTimeOffset.UtcNow) with
            { MediaAssets = [asset], MasterAudioAssetId = asset.Id, Accompaniments = [new(asset.Id, "accompaniments/song.wav", asset.Duration, null, DateTimeOffset.UtcNow)] };
            Field("_workspace").SetValue(window, new WorkspaceSession(root, project));
            ((TabControl)window.FindName("EditorTabs")).SelectedItem = window.FindName("PreviewTab");
            Call(window, "RefreshWorkspaceDisplay"); Assert.True(toggle.IsEnabled); Assert.False(toggle.IsChecked);
            Assert.Single(((ListBox)window.FindName("MediaLibraryList")).Items);
            Field("_previewAccompaniment").SetValue(window, true); Call(window, "RefreshAccompanimentState");
            Assert.True((bool)Property("UsePreviewAccompaniment").GetValue(window)!);
            var inspector = (TabControl)window.FindName("PreviewInspectorTabs");
            Assert.Equal(new[] { "节拍器", "伴奏" }, inspector.Items.Cast<TabItem>().Select(t => t.Header.ToString()));
            inspector.SelectedIndex = 1;
            foreach (var size in new[] { new Size(1440, 940), new Size(1100, 760) })
            {
                var content = (FrameworkElement)window.Content;
                content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                Assert.True(toggle.ActualWidth > 40); var point = toggle.TranslatePoint(new Point(), content);
                Assert.InRange(point.X + toggle.ActualWidth, 0, size.Width);
                Snapshot(content, $"accompaniment-preview-{size.Width}", size);
            }
            // No loaded backend in this UI test; changing modes must still choose the original song.
            ((TabControl)window.FindName("EditorTabs")).SelectedItem = window.FindName("RecordingTab");
            Assert.False((bool)Property("UsePreviewAccompaniment").GetValue(window)!);
            ((TabControl)window.FindName("EditorTabs")).SelectedItem = window.FindName("PreviewTab");
            Assert.True((bool)Property("UsePreviewAccompaniment").GetValue(window)!);
            File.Delete(Path.Combine(root, "accompaniments", "song.wav")); Call(window, "RefreshAccompanimentState");
            Assert.False(toggle.IsEnabled); Assert.False(toggle.IsChecked); Assert.Contains("丢失", toggle.ToolTip.ToString());
        }
        finally { Field("_allowClose").SetValue(window, true); window.Close(); Directory.Delete(root, true); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ExportDialogSupportsOneOrBothAndRequiresSelection(bool hasAccompaniment) => Sta(() =>
    {
        var window = new ExportVersionsWindow(hasAccompaniment);
        try
        {
            Assert.True(window.OriginalCheck.IsChecked); Assert.Equal(hasAccompaniment, window.AccompanimentCheck.IsEnabled);
            window.OriginalCheck.IsChecked = false; window.UpdateSelection(); Assert.False(window.ContinueButton.IsEnabled);
            window.AccompanimentCheck.IsChecked = true; window.UpdateSelection(); Assert.Equal(hasAccompaniment, window.ContinueButton.IsEnabled);
            window.OriginalCheck.IsChecked = true; window.UpdateSelection(); Assert.True(window.ContinueButton.IsEnabled);
            var content = (FrameworkElement)window.Content; var size = new Size(440, 330);
            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
            Snapshot(content, "export-versions-" + hasAccompaniment, size);
        }
        finally { window.Close(); }
    });

    private static void Snapshot(FrameworkElement element, string name, Size size)
    {
        if (Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR") is not { Length: > 0 } path) return;
        Directory.CreateDirectory(path); var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        image.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(path, name + ".png")); encoder.Save(output);
    }
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static PropertyInfo Property(string name) => typeof(MainWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Call(MainWindow window, string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    private static async Task Sta(Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completed.SetResult(); } catch (Exception e) { completed.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
