using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.App.Tests;

public sealed class WorkAreaUiTests
{
    [Fact]
    public Task ApplyUndoRedoAndResetPreserveLyricsAndPendingTaps() => Sta(async () =>
    {
        using var f = await Fixture.Create();
        var original = f.Project;
        var markers = (List<ProjectTime>)Field("_markers").GetValue(f.Window)!;
        markers.Add(ProjectTime.FromSeconds(0.25));
        f.SetRange("1.234567", "8.765432");
        await f.Task("ApplyWorkAreaAsync", false);
        var applied = f.Project.WorkArea;
        Assert.Equal(ProjectTime.FromSeconds(1.234567), applied!.Value.Start);
        Assert.Equal(applied.Value.Start, f.Playback.Position);
        Assert.Equal(original.Lyrics, f.Project.Lyrics);
        Assert.Equal(original.MediaAssets, f.Project.MediaAssets);
        Assert.Equal(ProjectTime.FromSeconds(0.25), Assert.Single(markers));
        await f.Task("RestoreHistoryAsync", false);
        Assert.Null(f.Project.WorkArea);
        Assert.Equal("10", f.Find<TextBox>("WorkAreaEndText").Text);
        await f.Task("RestoreHistoryAsync", true);
        Assert.Equal(applied, f.Project.WorkArea);
        await f.Task("ApplyWorkAreaAsync", true);
        Assert.Null(f.Project.WorkArea);
        Assert.Equal(10, f.Project.GetWorkArea().Duration.TotalSeconds);
    });

    [Theory]
    [InlineData("-1", "8")]
    [InlineData("0", "11")]
    [InlineData("4", "4")]
    [InlineData("5", "4")]
    [InlineData("NaN", "4")]
    [InlineData("0", "Infinity")]
    [InlineData("1", "1.0000001")]
    public Task InvalidDraftLeavesProjectAndPlaybackUntouched(string start, string end) => Sta(async () =>
    {
        using var f = await Fixture.Create();
        var original = f.Project;
        f.SetRange(start, end);
        await f.Task("ApplyWorkAreaAsync", false);
        Assert.Equal(original, f.Project);
        Assert.Contains("裁切未应用", f.Find<TextBlock>("StatusText").Text);
    });

    [Fact]
    public Task PreviewStopsAtRangeEndButRecordingKeepsFullClock() => Sta(async () =>
    {
        using var f = await Fixture.Create();
        f.SetRange("2", "8");
        await f.Task("ApplyWorkAreaAsync", false);
        await f.Playback.SeekAsync(ProjectTime.Zero);
        await f.Task("PrepareWorkAreaPlaybackAsync");
        Assert.Equal(ProjectTime.FromSeconds(2), f.Playback.Position);
        await f.Playback.PlayAsync();
        await f.Playback.SeekAsync(ProjectTime.FromSeconds(8.1));
        await f.Task("EnforceWorkAreaPlaybackAsync");
        Assert.False(f.Playback.IsPlaying);
        Assert.Equal(ProjectTime.FromSeconds(8), f.Playback.Position);
        await f.Task("PrepareWorkAreaPlaybackAsync");
        Assert.Equal(ProjectTime.FromSeconds(2), f.Playback.Position);
        f.Find<TabControl>("EditorTabs").SelectedItem = f.Find<TabItem>("RecordingTab");
        await f.Playback.SeekAsync(ProjectTime.FromSeconds(9));
        await f.Playback.PlayAsync();
        await f.Task("EnforceWorkAreaPlaybackAsync");
        Assert.True(f.Playback.IsPlaying);
        Assert.Equal(ProjectTime.FromSeconds(9), f.Playback.Position);
        Assert.Equal(Visibility.Collapsed, f.Find<WrapPanel>("WorkAreaPanel").Visibility);
    });

    [Fact]
    public Task PlayheadButtonsAreDraftsAndWorkAreaFitsMinimumWindow() => Sta(async () =>
    {
        using var f = await Fixture.Create();
        await f.Playback.SeekAsync(ProjectTime.FromSeconds(2));
        f.Call("WorkAreaStartHere_Click", f.Window, new RoutedEventArgs());
        await f.Playback.SeekAsync(ProjectTime.FromSeconds(8));
        f.Call("WorkAreaEndHere_Click", f.Window, new RoutedEventArgs());
        Assert.Null(f.Project.WorkArea);
        await f.Task("ApplyWorkAreaAsync", false);
        var content = (FrameworkElement)f.Window.Content;
        foreach (var size in new[] { new Size(1440, 940), new Size(1100, 760) })
        {
            content.Measure(size); content.Arrange(new Rect(new Point(), size)); content.UpdateLayout();
            var panel = f.Find<WrapPanel>("WorkAreaPanel");
            foreach (FrameworkElement child in panel.Children)
            {
                var point = child.TranslatePoint(new Point(), content);
                Assert.InRange(point.X + child.ActualWidth, 0, size.Width);
                Assert.InRange(point.Y + child.ActualHeight, 0, size.Height);
            }
            string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, $"work-area-{size.Width}.png")); encoder.Save(output);
            }
        }
    });

    private sealed class Fixture : IDisposable
    {
        public MainWindow Window { get; } = new();
        public MetronomeWorkflowTests.Playback Playback { get; } = new() { Duration = ProjectDuration.FromSeconds(10) };
        public KaraokeProject Project => ((WorkspaceSession)Field("_workspace").GetValue(Window)!).Project;
        public static async Task<Fixture> Create()
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var f = new Fixture();
            ((DispatcherTimer)Field("_uiTimer").GetValue(f.Window)!).Stop();
            ((DispatcherTimer)Field("_recoveryTimer").GetValue(f.Window)!).Stop();
            await ((IAudioPlaybackSession)Field("_playback").GetValue(f.Window)!).DisposeAsync();
            Field("_playback").SetValue(f.Window, f.Playback);
            var song = new MediaAsset(Guid.NewGuid(), "song", "song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(10));
            var project = KaraokeProject.Create("工作区裁切", DateTimeOffset.UtcNow) with
            {
                MediaAssets = [song], MasterAudioAssetId = song.Id,
                Lyrics = new LyricImportService().Parse("春风", ".txt", LyricLanguage.Chinese),
            };
            Field("_workspace").SetValue(f.Window, new WorkspaceSession(Path.GetTempPath(), project));
            f.Find<TabControl>("EditorTabs").SelectedItem = f.Find<TabItem>("PreviewTab");
            foreach (string method in new[] { "RefreshLyricRows", "RefreshVideoRows", "RefreshSubtitleStyleFields", "RefreshExportPreset" }) { f.Call(method); }
            return f;
        }
        public T Find<T>(string name) => (T)Window.FindName(name);
        public void SetRange(string start, string end) { Find<TextBox>("WorkAreaStartText").Text = start; Find<TextBox>("WorkAreaEndText").Text = end; }
        public object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, args);
        public Task Task(string name, params object?[] args) => (Task)Call(name, args)!;
        public void Dispose() { Field("_allowClose").SetValue(Window, true); Window.Close(); }
    }
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static async Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
