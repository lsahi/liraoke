using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Tests;

public sealed class MediaCreationWorkflowTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1080)]
    [InlineData(1, 0, 1440, 1080)]
    [InlineData(2, 0, 1080, 1080)]
    [InlineData(3, 0, 1080, 1920)]
    [InlineData(0, 1, 1280, 720)]
    [InlineData(1, 1, 960, 720)]
    [InlineData(2, 1, 720, 720)]
    [InlineData(3, 1, 720, 1280)]
    public Task AspectAndResolutionAreIndependentAndRoundTripWithoutChangingTimings(int ratio, int resolution, int width, int height) => Sta(async () =>
    {
        using var f = await Fixture.Create();
        var lyrics = f.Project.Lyrics;
        var clips = f.Project.VideoClips;
        ((ComboBox)f.Window.FindName("ExportAspectRatioCombo")).SelectedIndex = ratio;
        ((ComboBox)f.Window.FindName("ExportResolutionCombo")).SelectedIndex = resolution;
        ((ComboBox)f.Window.FindName("ExportFrameRateCombo")).SelectedIndex = 2;
        Assert.Equal(width, f.Project.ExportPreset.Width);
        Assert.Equal(height, f.Project.ExportPreset.Height);
        Assert.Equal(60, f.Project.ExportPreset.FramesPerSecond);
        var canvas = (Grid)f.Window.FindName("PreviewCanvas");
        Assert.Equal(width, canvas.Width);
        Assert.Equal(height, canvas.Height);
        Assert.Equal($"{width} × {height}", ((TextBlock)f.Window.FindName("ExportDimensionsText")).Text);
        Assert.Same(lyrics, f.Project.Lyrics);
        Assert.Same(clips, f.Project.VideoClips);
        f.Call("ApplyExportPreset");
        var repository = new JsonProjectRepository();
        await repository.SaveAsync(f.Root, f.Project);
        f.Project = await repository.LoadAsync(f.Root);
        var beforeRefresh = f.Project;
        f.Refresh();
        Assert.Equal(beforeRefresh, f.Project); // Refreshing a portrait project must not transiently commit landscape dimensions.
        Assert.Equal(ratio, ((ComboBox)f.Window.FindName("ExportAspectRatioCombo")).SelectedIndex);
        Assert.Equal(resolution, ((ComboBox)f.Window.FindName("ExportResolutionCombo")).SelectedIndex);
        Assert.Equal(width, canvas.Width); Assert.Equal(height, canvas.Height);
        Snapshot(f.Window, $"output-{width}x{height}-1100.png", new Size(1100, 760));
    });

    [Fact]
    public Task OutputFormatChangesCanBeUndoneAndRedone() => Sta(async () =>
    {
        using var f = await Fixture.Create();
        ((ComboBox)f.Window.FindName("ExportAspectRatioCombo")).SelectedIndex = 3;
        Assert.Equal(1920, f.Project.ExportPreset.Height);
        await f.Task("RestoreHistoryAsync", false);
        Assert.Equal(1920, f.Project.ExportPreset.Width);
        Assert.Equal(1080, ((Grid)f.Window.FindName("PreviewCanvas")).Height);
        Assert.Equal(0, ((ComboBox)f.Window.FindName("ExportAspectRatioCombo")).SelectedIndex);
        await f.Task("RestoreHistoryAsync", true);
        Assert.Equal(1080, f.Project.ExportPreset.Width);
        Assert.Equal(1920, ((Grid)f.Window.FindName("PreviewCanvas")).Height);
    });

    [Fact]
    public Task PortraitImagePreviewFillsFrameAndCropsSymmetricallyWithoutStretching() => Sta(async () =>
    {
        using var f = await Fixture.Create();
        string image = Path.Combine(f.Root, "crop-pattern.png");
        var pixels = new byte[300 * 100 * 4];
        for (int y = 0; y < 100; y++)
        {
            for (int x = 0; x < 300; x++)
            {
                int offset = (y * 300 + x) * 4;
                pixels[offset + (x < 100 ? 2 : x < 200 ? 1 : 0)] = 255;
                pixels[offset + 3] = 255;
            }
        }
        var source = BitmapSource.Create(300, 100, 96, 96, PixelFormats.Bgra32, null, pixels, 300 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using (var output = File.Create(image)) { encoder.Save(output); }
        await f.Task("ImportImageAsync", image);
        ((ComboBox)f.Window.FindName("ExportAspectRatioCombo")).SelectedIndex = 3;
        f.Call("RefreshVideoPreview", ProjectTime.Zero);
        var preview = (Image)f.Window.FindName("StillImagePreview");
        Assert.Equal(Stretch.UniformToFill, preview.Stretch);
        var canvas = (Grid)f.Window.FindName("PreviewCanvas");
        canvas.Measure(new Size(1080, 1920)); canvas.Arrange(new Rect(0, 0, 1080, 1920)); canvas.UpdateLayout();
        var rendered = new RenderTargetBitmap(1080, 1920, 96, 96, PixelFormats.Pbgra32); rendered.Render(canvas);
        foreach (var point in new[] { new Point(2, 2), new Point(1077, 2), new Point(2, 1917), new Point(1077, 1917), new Point(540, 960) })
        {
            var pixel = new byte[4];
            rendered.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixel); // Only the centered green region remains; no black letterbox or distorted side stripes.
        }
        Snapshot(f.Window, "portrait-image-cover-1440.png", new Size(1440, 940));
    });

    [Fact]
    public Task ImageImportPreviewInspectorDuplicateAndPersistenceWorkTogether() => Sta(async () =>
    {
        using var f = await Fixture.Create();
        string image = Path.Combine(f.Root, "source.png");
        byte[] pixels = Enumerable.Repeat(new byte[] { 220, 180, 110, 255 }, 160 * 90).SelectMany(value => value).ToArray();
        var bitmap = BitmapSource.Create(160, 90, 96, 96, PixelFormats.Bgra32, null, pixels, 160 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(image)) { encoder.Save(output); }
        await f.Task("ImportImageAsync", image);
        var asset = Assert.Single(f.Project.MediaAssets);
        Assert.Equal(MediaKind.Image, asset.Kind);
        Assert.Null(f.Project.MasterAudioAssetId);
        Assert.Equal(5, Assert.Single(f.Project.VideoClips).Duration.TotalSeconds);
        Assert.NotEqual(image, Path.Combine(f.Root, asset.RelativePath));
        Assert.True(File.Exists(Path.Combine(f.Root, asset.RelativePath)));
        var grid = (DataGrid)f.Window.FindName("VideoGrid");
        var row = Assert.Single(grid.Items.Cast<object>());
        Assert.False((bool)row.GetType().GetProperty("HasSourceTiming")!.GetValue(row)!);
        row.GetType().GetProperty("DurationSeconds")!.SetValue(row, 12d);
        f.Call("ApplyVideoRowsToProject");
        f.Call("RefreshVideoRows");
        f.Call("RefreshVideoPreview", ProjectTime.FromSeconds(10));
        var preview = (Image)f.Window.FindName("StillImagePreview");
        Assert.Equal(Visibility.Visible, preview.Visibility);
        Assert.NotNull(preview.Source);
        Assert.Null(((MediaElement)f.Window.FindName("VideoPreview")).Source);
        f.Call("RefreshVideoPreview", ProjectTime.FromSeconds(12));
        Assert.Null(preview.Source);
        f.Call("RefreshVideoPreview", ProjectTime.Zero);
        var library = (ListBox)f.Window.FindName("MediaLibraryList");
        library.SelectedIndex = 0;
        f.Call("MediaLibraryList_MouseDoubleClick", library, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
        Assert.Equal(2, f.Project.VideoClips.Count);
        Assert.Equal(12, f.Project.VideoClips[1].TimelineStart.TotalSeconds);
        Assert.Equal(5, f.Project.VideoClips[1].Duration.TotalSeconds);
        Assert.Single(f.Project.MediaAssets);
        var repository = new JsonProjectRepository();
        await repository.SaveAsync(f.Root, f.Project);
        var saved = await repository.LoadAsync(f.Root);
        Assert.Equal(f.Project.VideoClips, saved.VideoClips);
        Snapshot(f.Window, "creation-images-preview-1440.png", new Size(1440, 940));
        ((TabControl)f.Window.FindName("EditorTabs")).SelectedItem = f.Window.FindName("RecordingTab");
        f.Call("RefreshVideoPreview", ProjectTime.Zero);
        Assert.Null(preview.Source); // A background image must not replace the original-media-only recording monitor.
    });

    [Theory]
    [InlineData(-1, 1, 1, 6)]
    [InlineData(-1, 3, 3, 4)]
    [InlineData(1, 15, 2, 13)]
    [InlineData(1, 4, 2, 2)]
    public Task ImageDragHandlesDoNotApplyVideoSourceDurationLimits(int handle, double cursor, double start, double length) => Sta(async () =>
    {
        using var f = await Fixture.Create();
        var asset = new MediaAsset(Guid.NewGuid(), "still.png", "media/images/still.png", MediaKind.Image, ProjectDuration.Zero);
        var clip = new VideoClip(Guid.NewGuid(), asset.Id, ProjectTime.FromSeconds(2), ProjectTime.Zero, ProjectDuration.FromSeconds(5), true);
        f.Project = f.Project with { MediaAssets = [asset], VideoClips = [clip] };
        var timeline = (ProjectTimelineControl)f.Window.FindName("ProjectTimeline");
        timeline.Measure(new Size(832, 144)); timeline.Arrange(new Rect(0, 0, 832, 144));
        timeline.Update(f.Project, ProjectTime.Zero);
        void Set(string name, object value) => typeof(ProjectTimelineControl).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(timeline, value);
        Set("_original", new TimelineDrag(clip.Id, null, clip.TimelineStart, clip.SourceStart, clip.Duration));
        Set("_handle", handle); Set("_dragTime", handle == -1 ? 2d : 7d);
        typeof(ProjectTimelineControl).GetMethod("UpdateDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(timeline, [new Point(132 + cursor * 100, 75)]);
        var changed = (TimelineDrag)typeof(ProjectTimelineControl).GetField("_drag", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(timeline)!;
        Assert.Equal(start, changed.Start.TotalSeconds);
        Assert.Equal(length, changed.Duration.TotalSeconds);
        Assert.Equal(ProjectTime.Zero, changed.SourceStart);
    });

    [Fact]
    public Task SpectrumToggleUsesMasterAndIsUndoableWithoutTouchingTimings() => Sta(async () =>
    {
        using var f = await Fixture.Create();
        var master = new MediaAsset(Guid.NewGuid(), "song.wav", "media/audio/song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(3));
        f.Project = f.Project with { MasterAudioAssetId = master.Id, MediaAssets = [master] };
        int calls = 0;
        var data = new AudioVisualizationData(3, Enumerable.Repeat((byte)180, 3 * 30 * 32).ToArray());
        f.Set("_audioVisualizationLoader", (Func<string, string, CancellationToken, Task<AudioVisualizationData>>)((source, root, token) =>
        {
            calls++; Assert.Equal(Path.GetFullPath(Path.Combine(f.Root, master.RelativePath)), source); Assert.Equal(f.Root, root);
            return Task.FromResult(data);
        }));
        f.Set("_playbackRate", 0.75);
        f.Refresh();
        await f.Task("SetAudioVisualizationAsync", true);
        Assert.Equal(1, calls); Assert.True(f.Project.AudioVisualizationEnabled);
        Assert.Equal(Visibility.Visible, ((AudioVisualizationOverlay)f.Window.FindName("AudioVisualizationPreview")).Visibility);
        ((TabControl)f.Window.FindName("EditorTabs")).SelectedItem = f.Window.FindName("RecordingTab");
        Assert.Equal(Visibility.Collapsed, ((AudioVisualizationOverlay)f.Window.FindName("AudioVisualizationPreview")).Visibility);
        ((TabControl)f.Window.FindName("EditorTabs")).SelectedItem = f.Window.FindName("PreviewTab");
        await f.Task("SetAudioVisualizationAsync", false);
        Assert.Equal(1, calls); Assert.Null(f.Get("_audioVisualizationData"));
        Assert.False(f.Project.AudioVisualizationEnabled);
        await f.Task("RestoreHistoryAsync", false);
        Assert.True(f.Project.AudioVisualizationEnabled); Assert.Equal(2, calls);
        var repository = new JsonProjectRepository(); await repository.SaveAsync(f.Root, f.Project);
        Assert.True((await repository.LoadAsync(f.Root)).AudioVisualizationEnabled);
        Snapshot(f.Window, "creation-spectrum-preview-1440.png", new Size(1440, 940));
    });

    [Fact]
    public Task LegacyFireworksStayOffAndCanBeReplacedWithoutChangingLyrics() => Sta(async () =>
    {
        using var f = await Fixture.Create();
        f.Project = f.Project with { AudioVisualizationEnabled = true, AudioVisualizationStyle = AudioVisualizationStyle.Fireworks };
        var original = f.Project;
        f.Refresh();
        await f.Task("PrepareAudioVisualizationAsync", f.Project, false);
        Assert.False(((CheckBox)f.Window.FindName("AudioVisualizationCheck")).IsChecked);
        Assert.Contains("已停用", ((TextBlock)f.Window.FindName("AudioVisualizationStatusText")).Text);
        Assert.Equal(Visibility.Collapsed, ((AudioVisualizationOverlay)f.Window.FindName("AudioVisualizationPreview")).Visibility);
        Assert.Equal(original, f.Project);
        await f.Task("SetAudioVisualizationStyleAsync", AudioVisualizationStyle.Fireworks);
        Assert.Equal(original, f.Project);
        await f.Task("SetAudioVisualizationAsync", true);
        Assert.Equal(AudioVisualizationStyle.Classic, f.Project.AudioVisualizationStyle);
        Assert.Equal(original.Lyrics, f.Project.Lyrics);
        Assert.Equal(original.Metronome, f.Project.Metronome);
    });

    [Fact]
    public Task VisualizationStylesSwitchWithoutReanalysisAndPersistThroughUndo() => Sta(async () =>
    {
        using var f = await Fixture.Create();
        var master = new MediaAsset(Guid.NewGuid(), "song.wav", "media/audio/song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(3));
        f.Project = f.Project with { MasterAudioAssetId = master.Id, MediaAssets = [master] };
        int calls = 0;
        var data = new AudioVisualizationData(3, Enumerable.Repeat((byte)220, 32 * 90).ToArray());
        f.Set("_audioVisualizationLoader", (Func<string, string, CancellationToken, Task<AudioVisualizationData>>)((_, _, _) =>
        { calls++; return Task.FromResult(data); }));
        f.Refresh(); await f.Task("SetAudioVisualizationAsync", true);
        var lyrics = f.Project.Lyrics; var metronome = f.Project.Metronome;
        var combo = (ComboBox)f.Window.FindName("AudioVisualizationStyleCombo");
        Assert.Equal(4, combo.Items.Count);
        foreach (var style in Enum.GetValues<AudioVisualizationStyle>().Where(style => style.IsAvailable()))
        {
            await f.Task("SetAudioVisualizationStyleAsync", style);
            Assert.Equal(style, f.Project.AudioVisualizationStyle);
            Assert.Equal(style.ToString(), ((ComboBoxItem)combo.SelectedItem).Tag);
            Assert.True(combo.IsEnabled);
            Assert.Equal(lyrics, f.Project.Lyrics); Assert.Equal(metronome, f.Project.Metronome);
        }
        Assert.Equal(1, calls);
        await f.Task("RestoreHistoryAsync", false);
        Assert.Equal(AudioVisualizationStyle.CenterWhite, f.Project.AudioVisualizationStyle);
        await f.Task("RestoreHistoryAsync", true);
        Assert.Equal(AudioVisualizationStyle.RotatingRing, f.Project.AudioVisualizationStyle);
        await f.Task("SetAudioVisualizationAsync", false);
        Assert.Equal(AudioVisualizationStyle.RotatingRing, f.Project.AudioVisualizationStyle);
        await f.Task("SetAudioVisualizationAsync", true);
        Assert.Equal(AudioVisualizationStyle.RotatingRing, f.Project.AudioVisualizationStyle);
        var repository = new JsonProjectRepository(); await repository.SaveAsync(f.Root, f.Project);
        Assert.Equal(AudioVisualizationStyle.RotatingRing, (await repository.LoadAsync(f.Root)).AudioVisualizationStyle);
        ((TabControl)f.Window.FindName("EditorTabs")).SelectedItem = f.Window.FindName("RecordingTab");
        Assert.Equal(Visibility.Collapsed, ((AudioVisualizationOverlay)f.Window.FindName("AudioVisualizationPreview")).Visibility);
        ((TabControl)f.Window.FindName("EditorTabs")).SelectedItem = f.Window.FindName("PreviewTab");
        Snapshot(f.Window, "visualization-selector-1440.png", new Size(1440, 940));
        Snapshot(f.Window, "visualization-selector-1100.png", new Size(1100, 760));
    });

    [Fact]
    public Task PausedOverlayRepaintsWhenOnlyStyleChanges() => Sta(() =>
    {
        var overlay = new AudioVisualizationOverlay();
        var data = new AudioVisualizationData(1, Enumerable.Repeat((byte)230, 32 * 30).ToArray());
        overlay.Measure(new Size(640, 360)); overlay.Arrange(new Rect(0, 0, 640, 360));
        byte[] Render(AudioVisualizationStyle style)
        {
            overlay.Update(data, ProjectTime.FromSeconds(0.3), style); overlay.UpdateLayout();
            var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32); bitmap.Render(overlay);
            byte[] pixels = new byte[640 * 360 * 4]; bitmap.CopyPixels(pixels, 640 * 4, 0); return pixels;
        }
        byte[] first = Render(AudioVisualizationStyle.CenterWhite), second = Render(AudioVisualizationStyle.RotatingRing);
        Assert.False(first.SequenceEqual(second)); Assert.Equal(first, Render(AudioVisualizationStyle.CenterWhite));
        return Task.CompletedTask;
    });

    [Fact]
    public Task CancelledSpectrumPreparationLeavesToggleAndProjectUnchanged() => Sta(async () =>
    {
        using var f = await Fixture.Create();
        var master = new MediaAsset(Guid.NewGuid(), "song.wav", "media/audio/song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(3));
        f.Project = f.Project with { MasterAudioAssetId = master.Id, MediaAssets = [master] };
        var before = f.Project;
        f.Set("_audioVisualizationLoader", (Func<string, string, CancellationToken, Task<AudioVisualizationData>>)((_, _, _) =>
            throw new OperationCanceledException()));
        await f.Task("SetAudioVisualizationAsync", true);
        Assert.Equal(before, f.Project);
        Assert.False(((CheckBox)f.Window.FindName("AudioVisualizationCheck")).IsChecked);
        Assert.Null(f.Get("_audioVisualizationData"));
        Assert.False((bool)f.Get("_isBusy")!);
    });

    [Fact]
    public Task MediaTypeIconsAreDistinctAndScaleWithoutBitmapAssets() => Sta(async () =>
    {
        using var f = await Fixture.Create();
        var outputs = new List<byte[]>();
        foreach (var kind in Enum.GetValues<MediaKind>())
        {
            var icon = new MediaKindIcon { Kind = kind, Width = 48, Height = 48 };
            icon.Measure(new Size(48, 48)); icon.Arrange(new Rect(0, 0, 48, 48));
            var bitmap = new RenderTargetBitmap(48, 48, 96, 96, PixelFormats.Pbgra32); bitmap.Render(icon);
            var pixels = new byte[48 * 48 * 4]; bitmap.CopyPixels(pixels, 48 * 4, 0);
            Assert.Contains(pixels, item => item != 0); outputs.Add(pixels);
        }
        Assert.False(outputs[0].SequenceEqual(outputs[1])); Assert.False(outputs[1].SequenceEqual(outputs[2]));
        Assert.False(outputs[0].SequenceEqual(outputs[2]));
    });

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Liraoke.CreationUi", Guid.NewGuid().ToString("N"));
        public MainWindow Window { get; } = new();
        public KaraokeProject Project
        {
            get => ((WorkspaceSession)Get("_workspace")!).Project;
            set => Set("_workspace", new WorkspaceSession(Root, value));
        }
        public object? Get(string name) => Field(name).GetValue(Window);
        public void Set(string name, object? value) => Field(name).SetValue(Window, value);
        public object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, args);
        public Task Task(string name, params object?[] args) => (Task)Call(name, args)!;
        public void Refresh()
        {
            foreach (string method in new[] { "RefreshLyricRows", "RefreshVideoRows", "RefreshSubtitleStyleFields", "RefreshExportPreset", "RefreshWorkspaceDisplay" }) { Call(method); }
        }
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root);
            ((DispatcherTimer)f.Get("_uiTimer")!).Stop(); ((DispatcherTimer)f.Get("_recoveryTimer")!).Stop();
            await ((IAudioPlaybackSession)f.Get("_playback")!).DisposeAsync();
            f.Set("_playback", new MetronomeWorkflowTests.Playback());
            f.Project = KaraokeProject.Create("图片与频谱", DateTimeOffset.UtcNow);
            ((TabControl)f.Window.FindName("EditorTabs")).SelectedItem = f.Window.FindName("PreviewTab");
            f.Refresh(); return f;
        }
        public void Dispose() { Set("_allowClose", true); Window.Close(); Directory.Delete(Root, true); }
        private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    }

    private static void Snapshot(MainWindow window, string name, Size size)
    {
        string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) { return; }
        var content = (FrameworkElement)window.Content;
        content.Measure(size); content.Arrange(new Rect(new Point(), size)); content.UpdateLayout();
        if (name.StartsWith("visualization-selector", StringComparison.Ordinal))
        {
            var scroll = (ScrollViewer)window.FindName("PreviewSettingsScroll");
            typeof(MainWindow).GetMethod("ScrollToPreviewSection", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, ["VisualizationSection"]); content.UpdateLayout();
            var combo = (ComboBox)window.FindName("AudioVisualizationStyleCombo");
            var point = combo.TranslatePoint(new Point(), scroll);
            Assert.InRange(point.Y, 0, scroll.ActualHeight);
            Assert.InRange(point.Y + combo.ActualHeight, 0, scroll.ActualHeight);
        }
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory); using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }

    private static async Task Sta(Func<Task> action)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); complete.TrySetResult(); }
                catch (Exception error) { complete.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}
