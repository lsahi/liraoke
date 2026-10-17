using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.App.Dialogs;

namespace KaraokeFilmMaker.App.Tests;

public sealed class SaveChangesWindowTests
{
    [Fact]
    public Task SaveDiscardAndCancelAreExplicitWithSafeKeyboardDefaults() => RunSta(() =>
    {
        var window = new SaveChangesWindow("工作区有未保存的更改。保存后再关闭吗？");
        try
        {
            var buttons = Descendants(window).OfType<Button>().ToArray();
            Assert.Equal(new[] { "保存", "不保存", "取消" }, buttons.Select(button => button.Content));
            Assert.All(buttons, button =>
            {
                Assert.Equal(Visibility.Visible, button.Visibility);
                Assert.True(button.IsEnabled);
            });
            Assert.True(buttons[0].IsDefault);
            Assert.False(buttons[0].IsCancel);
            Assert.False(buttons[1].IsDefault);
            Assert.False(buttons[1].IsCancel);
            Assert.False(buttons[2].IsDefault);
            Assert.True(buttons[2].IsCancel);
            Assert.Equal(MessageBoxResult.Cancel, window.Choice);
            Assert.False(window.IsVisible);
            SaveOptionalSnapshot(window);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("保存", MessageBoxResult.Yes)]
    [InlineData("不保存", MessageBoxResult.No)]
    [InlineData("取消", MessageBoxResult.Cancel)]
    public Task ButtonClickReturnsTheCorrespondingChoice(string label, MessageBoxResult expected) => RunSta(() =>
    {
        var window = new SaveChangesWindow("工作区有未保存的更改。");
        try
        {
            var button = Assert.Single(Descendants(window).OfType<Button>(), item => Equals(item.Content, label));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(expected, window.Choice);
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ClosingWithoutChoosingPreservesCancel() => RunSta(() =>
    {
        var window = new SaveChangesWindow("工作区有未保存的更改。");
        window.Close();
        Assert.Equal(MessageBoxResult.Cancel, window.Choice);
        Assert.False(window.IsVisible);
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dependencyObject) { continue; }
            yield return dependencyObject;
            foreach (var descendant in Descendants(dependencyObject)) { yield return descendant; }
        }
    }

    private static void SaveOptionalSnapshot(SaveChangesWindow window)
    {
        string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) { return; }

        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, double.PositiveInfinity));
        int width = (int)Math.Ceiling(window.Width);
        int height = (int)Math.Ceiling(content.DesiredSize.Height);
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, "close-save-dialog.png"));
        encoder.Save(file);
    }

    private static async Task RunSta(Action action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); finished.TrySetResult(); }
            catch (Exception exception) { finished.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
