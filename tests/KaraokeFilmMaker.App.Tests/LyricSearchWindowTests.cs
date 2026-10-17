using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Abstractions;

namespace KaraokeFilmMaker.App.Tests;

public sealed class LyricSearchWindowTests
{
    [Theory]
    [InlineData("自作テスト曲")]
    [InlineData("My song")]
    public async Task DefaultsToLrclibAndBothRemainingTabsPrepareTheCorrectImport(string title)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            LyricSearchWindow? window = null;
            try
            {
                window = new LyricSearchWindow(new StubSearch(), title, 12);
                var tabs = (TabControl)window.FindName("SearchTabs");
                Assert.Equal(0, tabs.SelectedIndex); Assert.Equal(2, tabs.Items.Count);
                Assert.Equal(new[] { "LRCLIB（软件内搜索）", "粘贴已授权歌词" }, tabs.Items.Cast<TabItem>().Select(tab => tab.Header));
                Assert.Same(window.FindName("LrclibTab"), tabs.SelectedItem);
                typeof(LyricSearchWindow).GetMethod("SearchButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [window, new RoutedEventArgs()]);
                var grid = (DataGrid)window.FindName("ResultsGrid");
                Assert.Single(grid.Items.Cast<object>());
                grid.SelectedIndex = 0;
                Assert.Contains("[00:01.00]", ((TextBox)window.FindName("ResultPreviewText")).Text);
                Assert.Contains("相差 1.0 秒", ((TextBlock)window.FindName("ResultInfoText")).Text);
                Assert.False(((CheckBox)window.FindName("RightsCheck")).IsChecked);
                Assert.Null(window.Selection);
                typeof(LyricSearchWindow).GetMethod("ImportSelectedButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [window, new RoutedEventArgs()]);
                Assert.Contains("权限", ((TextBlock)window.FindName("ImportStatus")).Text);
                var prepare = typeof(LyricSearchWindow).GetMethod("TryPrepareImport", BindingFlags.Instance | BindingFlags.NonPublic)!;
                ((CheckBox)window.FindName("RightsCheck")).IsChecked = true;
                Assert.True((bool)prepare.Invoke(window, null)!);
                Assert.True(window.Selection!.UseLrc);
                Assert.Equal("LRCLIB", window.Selection.Provider);
                Assert.Equal("[00:01.00]自作の歌", window.Selection.Text);
                ((CheckBox)window.FindName("UseTimingCheck")).IsChecked = false;
                typeof(LyricSearchWindow).GetMethod("RefreshPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                Assert.Equal("自作の歌", ((TextBox)window.FindName("ResultPreviewText")).Text);
                Assert.True((bool)prepare.Invoke(window, null)!);
                Assert.False(window.Selection!.UseLrc);
                Assert.Equal("自作の歌", window.Selection.Text);
                string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                    var content = (FrameworkElement)window.Content;
                    content.Measure(new Size(1080, 760)); content.Arrange(new Rect(0, 0, 1080, 760)); content.UpdateLayout();
                    var image = new RenderTargetBitmap(1080, 760, 96, 96, PixelFormats.Pbgra32); image.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var file = File.Create(Path.Combine(directory, "lyric-search.png")); encoder.Save(file);
                }
                tabs.SelectedItem = window.FindName("PasteTab");
                ((CheckBox)window.FindName("RightsCheck")).IsChecked = false;
                ((TextBox)window.FindName("PasteLyricsInput")).Text = "自分の歌詞";
                Assert.False((bool)prepare.Invoke(window, null)!);
                Assert.Null(window.Selection);
                ((CheckBox)window.FindName("RightsCheck")).IsChecked = true;
                ((TextBox)window.FindName("PasteSourceInput")).Text = "https://example.com/lyrics";
                Assert.True((bool)prepare.Invoke(window, null)!);
                Assert.Equal("User supplied", window.Selection!.Provider);
                Assert.Equal("自分の歌詞", window.Selection.Text);
                Assert.Equal("https://example.com/lyrics", window.Selection.SourceUrl);
                ((TextBox)window.FindName("PasteSourceInput")).Text = "javascript:alert(1)";
                Assert.False((bool)prepare.Invoke(window, null)!);
                Assert.Null(window.Selection);
                window.Close();
                window = new LyricSearchWindow(new StubSearch(fail: true), "自作", null);
                typeof(LyricSearchWindow).GetMethod("SearchButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [window, new RoutedEventArgs()]);
                Assert.Contains("响应格式", ((TextBlock)window.FindName("SearchStatus")).Text);
                Assert.True(((Button)window.FindName("SearchButton")).IsEnabled);
                finished.TrySetResult();
            }
            catch (Exception exception) { finished.TrySetException(exception); }
            finally { window?.Close(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class StubSearch(bool fail = false) : ILyricSearchService
    {
        public Task<IReadOnlyList<OnlineLyricResult>> SearchAsync(LyricSearchQuery query, CancellationToken cancellationToken = default) =>
            fail ? Task.FromException<IReadOnlyList<OnlineLyricResult>>(new InvalidDataException("响应格式发生变化"))
                : Task.FromResult<IReadOnlyList<OnlineLyricResult>>([new(17, "自作テスト曲", "demo", "自作アルバム", 13, false,
                "自作の歌", "[00:01.00]自作の歌", "LRCLIB", "https://lrclib.net/api/get/17")]);
    }
}
