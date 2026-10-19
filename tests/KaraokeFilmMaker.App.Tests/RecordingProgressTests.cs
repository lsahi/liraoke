using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Tests;

public sealed class RecordingProgressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RestartRecordingRequiresConfirmationAndKeepsEditedLyrics(bool accept) => RunSta(context =>
    {
        var project = Confirm(Confirm(Project("春风\n明天"), 0, 0, 1, 2), 0, 1, 2, 3);
        var first = project.Lyrics.Lines[0] with { TranslationText = "manually edited", ReadingText = "user reading" };
        project = project with { Lyrics = project.Lyrics with { Lines = [first, project.Lyrics.Lines[1]] } };
        var window = context.Open(project);
        Call(window, "RefreshSubtitleStyleFields"); Call(window, "RefreshExportPreset");
        Tap(window, 5); Tap(window, 6); Call(window, "UndoLastMarker");
        var undone = (Stack<(ProjectTime Time, ProjectTime Raw)>)Field("_undoneMarkers").GetValue(window)!;
        Assert.Single(undone);
        Field("_loopRange").SetValue(window, new TimeRange(At(1), At(3)));
        Field("_countdownTarget").SetValue(window, At(9));
        Playback(window).Position = At(6);
        Playback(window).PlayAsync().GetAwaiter().GetResult();
        var beforeRestart = Session(window).Project;
        int confirmations = 0;
        ((Task)Call(window, "RestartRecordingAsync", (Func<bool>)(() => { confirmations++; return accept; }))!).GetAwaiter().GetResult();
        Assert.Equal(1, confirmations);
        if (!accept)
        {
            Assert.Same(beforeRestart, Session(window).Project);
            Assert.Single(Markers(window)); Assert.Single(undone);
            Assert.True(Playback(window).IsPlaying); Assert.Equal(At(6), Playback(window).Position);
            return;
        }
        Assert.Empty(Markers(window)); Assert.Empty(undone);
        Assert.Empty((List<ProjectTime>)Field("_rawMarkers").GetValue(window)!);
        Assert.Null(Field("_loopRange").GetValue(window)); Assert.Null(Field("_countdownTarget").GetValue(window));
        Assert.False(Playback(window).IsPlaying); Assert.Equal(At(0), Playback(window).Position);
        Assert.Equal(first.Id, Current(window).LineId);
        Assert.Contains("0/4", Progress(window));
        Assert.Equal(first.TranslationText, Session(window).Project.Lyrics.Lines[0].TranslationText);
        Assert.Equal(first.ReadingText, Session(window).Project.Lyrics.Lines[0].ReadingText);
        Assert.All(Session(window).Project.Lyrics.Lines, line =>
        {
            Assert.Equal(0, line.Timing.Duration.Microseconds);
            Assert.All(line.Units, unit => { Assert.False(unit.IsConfirmed); Assert.Null(unit.RawInputTime); });
        });
        // Restoring applied times does not roll back the user's lyric edits.
        ((Task)Call(window, "RestoreHistoryAsync", false)!).GetAwaiter().GetResult();
        Assert.Equal(first.Timing, Session(window).Project.Lyrics.Lines[0].Timing);
        Assert.Equal(first.TranslationText, Session(window).Project.Lyrics.Lines[0].TranslationText);
        // Reset again, save, and reopen: no previously confirmed progress should come back.
        ((Task)Call(window, "RestartRecordingAsync", (Func<bool>)(() => true))!).GetAwaiter().GetResult();
        var repository = new JsonProjectRepository();
        repository.SaveAsync(context.Root, Session(window).Project).GetAwaiter().GetResult();
        var reopened = context.Open(repository.LoadAsync(context.Root).GetAwaiter().GetResult());
        Assert.Contains("0/4", Progress(reopened));
        Assert.Equal(first.Id, Current(reopened).LineId);
        Tap(reopened, 1); Tap(reopened, 2);
        Playback(reopened).Position = At(3);
        Call(reopened, "ApplyUnitMarkersButton_Click", reopened, new RoutedEventArgs());
        Assert.Contains("2/4", Progress(reopened));
    });

    [Fact]
    public Task WholeSongRestartIsSecondInTimingTransportOnly() => RunSta(context =>
    {
        var window = context.Open(Project("春风"));
        Assert.Equal("重新打轴", ((Button)window.FindName("RestartRecordingButton")).Content);
        Assert.Null(window.FindName("RestartRecordingFromTimingButton"));
        var button = (Button)window.FindName("RestartRecordingButton");
        var panel = Assert.IsType<WrapPanel>(button.Parent);
        Assert.Same(button, panel.Children[1]);
        Assert.Contains("播放", ((Button)panel.Children[0]).Content.ToString());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RestartDoesNotDiscardUnappliedLyricDrafts(bool sourceDraft) => RunSta(context =>
    {
        var window = context.Open(Project("春风"));
        var line = Session(window).Project.Lyrics.Lines[0];
        if (sourceDraft)
        {
            ((Task)Call(window, "BeginSourceLyricEditAsync", line.Id)!).GetAwaiter().GetResult();
            var row = ((DataGrid)window.FindName("LyricsGrid")).Items[0];
            row.GetType().GetProperty("SourceEditText")!.SetValue(row, "尚未应用的原文");
        }
        else
        {
            Field("_recordingEditTarget").SetValue(window, (line.Id, line.Units[0].Id));
            Call(window, "ShowQuickEdit");
            ((TextBox)window.FindName("QuickEditText")).Text = "尚未应用";
        }
        var before = Session(window).Project;
        int confirmations = 0;
        ((Task)Call(window, "RestartRecordingAsync", (Func<bool>)(() => { confirmations++; return true; }))!).GetAwaiter().GetResult();
        Assert.Equal(0, confirmations);
        Assert.Same(before, Session(window).Project);
        Assert.Contains("未应用", ((TextBlock)window.FindName("StatusText")).Text);
    });

    [Fact]
    public Task SavedProjectReopensAtFirstUnconfirmedUnitAndContinuingPreservesConfirmedTimes() => RunSta(context =>
    {
        var project = Project("春风\n明天\n再会");
        project = Confirm(project, 0, 0, 1, 2);
        project = Confirm(project, 0, 1, 2, 3);
        project = Confirm(project, 1, 0, 5, 6);
        var original = context.Open(project);
        var repository = new JsonProjectRepository();
        repository.SaveAsync(context.Root, Session(original).Project).GetAwaiter().GetResult();
        context.Close(original);

        // A new window and a disk-loaded project must not depend on any previous tap buffer.
        var loaded = repository.LoadAsync(context.Root).GetAwaiter().GetResult();
        var reopened = context.Open(loaded);
        var current = Current(reopened);
        Assert.Contains("3/6", Progress(reopened));
        Assert.Contains("1/3 句完成", Progress(reopened));
        Assert.Equal(loaded.Lyrics.Lines[1].Id, current.LineId);
        Assert.Equal(1, current.ActiveUnitIndex);
        Assert.Contains("天", Hint(reopened));
        Assert.Empty(Markers(reopened));
        SaveOptionalSnapshot(reopened);

        Tap(reopened, 6); Tap(reopened, 8); Tap(reopened, 9); Tap(reopened, 10);
        Playback(reopened).Position = At(10);
        Call(reopened, "ApplyUnitMarkersButton_Click", reopened, new RoutedEventArgs());
        var result = Session(reopened).Project;
        for (int index = 0; index < 2; index++)
        { AssertSavedUnitUnchanged(loaded.Lyrics.Lines[0].Units[index], result.Lyrics.Lines[0].Units[index]); }
        AssertSavedUnitUnchanged(loaded.Lyrics.Lines[1].Units[0], result.Lyrics.Lines[1].Units[0]);
        Assert.Equal(At(6), result.Lyrics.Lines[1].Units[1].Timing.Start);
        Assert.Equal(new[] { At(8), At(9) }, result.Lyrics.Lines[2].Units.Select(unit => unit.Timing.Start));
        Assert.All(result.Lyrics.Lines.SelectMany(line => line.Units), unit => Assert.True(unit.IsConfirmed));
        Assert.Empty(Markers(reopened));
        Assert.Contains("6/6", Progress(reopened));
        Assert.Contains("全部打轴已完成", Hint(reopened));
    });

    [Fact]
    public Task CompletedSongRequiresExplicitRerecordAndCanReturnToProtectedCompletion() => RunSta(context =>
    {
        var project = Project("春风\n明天");
        for (int line = 0; line < 2; line++)
        for (int unit = 0; unit < 2; unit++)
        { project = Confirm(project, line, unit, 1 + line * 3 + unit, 2 + line * 3 + unit); }
        var repository = new JsonProjectRepository();
        repository.SaveAsync(context.Root, project).GetAwaiter().GetResult();
        var window = context.Open(repository.LoadAsync(context.Root).GetAwaiter().GetResult());
        Assert.Contains("4/4", Progress(window));
        Assert.Contains("全部打轴已完成", Hint(window));
        Assert.Equal(-1, Current(window).ActiveUnitIndex);
        Assert.False((bool)Call(window, "RecordMarker", At(10))!);
        Assert.Empty(Markers(window));

        Call(window, "BrowseRecordingLines", -999);
        Call(window, "RecordFromBrowsedLine_Click", window, new RoutedEventArgs());
        Assert.Equal(project.Lyrics.Lines[0].Id, Current(window).LineId);
        Assert.Equal(0, Current(window).ActiveUnitIndex);
        Tap(window, 10); Tap(window, 11);
        Playback(window).Position = At(12);
        Call(window, "ApplyUnitMarkersButton_Click", window, new RoutedEventArgs());
        var result = Session(window).Project;
        Assert.Equal(new[] { At(10), At(11) }, result.Lyrics.Lines[0].Units.Select(unit => unit.Timing.Start));
        for (int index = 0; index < 2; index++)
        { AssertSavedUnitUnchanged(project.Lyrics.Lines[1].Units[index], result.Lyrics.Lines[1].Units[index]); }

        Call(window, "ContinueUnfinishedRecording_Click", window, new RoutedEventArgs());
        Assert.Contains("全部打轴已完成", Hint(window));
        Assert.False((bool)Call(window, "RecordMarker", At(13))!);
        Assert.Empty(Markers(window));
    });

    [Fact]
    public Task BrowsingShowsSavedUnitsInGreenAndClearingPendingTapsDoesNotClearSavedProgress() => RunSta(context =>
    {
        var project = Confirm(Confirm(Project("春风\n明天"), 0, 0, 1, 2), 0, 1, 2, 3);
        var window = context.Open(project);
        Assert.Equal(project.Lyrics.Lines[1].Id, Current(window).LineId);
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        Call(window, "RecordingLyrics_MouseWheel", window, wheel);
        Assert.True(wheel.Handled);
        Assert.Equal(project.Lyrics.Lines[0].Id, Current(window).LineId);
        Assert.Equal(-1, Current(window).ActiveUnitIndex);
        Assert.True(ContainsGreenGlyphs(Current(window)));
        Assert.Empty(Markers(window));

        Tap(window, 5);
        Assert.Equal(project.Lyrics.Lines[1].Id, Current(window).LineId);
        Assert.Single(Markers(window));
        Assert.Contains("2/4", Progress(window));
        Call(window, "UndoLastMarker");
        Call(window, "ClearMarkersButton_Click", window, new RoutedEventArgs());
        Assert.Empty(Markers(window));
        Assert.Contains("2/4", Progress(window));
        Assert.Equal(0, Current(window).ActiveUnitIndex);
        Assert.Contains("明", Hint(window));
        for (int index = 0; index < 2; index++)
        { AssertSavedUnitUnchanged(project.Lyrics.Lines[0].Units[index], Session(window).Project.Lyrics.Lines[0].Units[index]); }
        Call(window, "RedoMarkerButton_Click", window, new RoutedEventArgs());
        Assert.Empty(Markers(window));
    });

    [Fact]
    public Task ImportedLrcEstimatedUnitTimesDoNotCountAsAppliedRecording() => RunSta(context =>
    {
        var project = KaraokeProject.Create("LRC estimates", DateTimeOffset.UtcNow) with
        { Lyrics = new LyricImportService().Parse("[00:01]春风\n[00:05]明天", ".lrc", LyricLanguage.Chinese) };
        Assert.Contains(project.Lyrics.Lines.SelectMany(line => line.Units), unit => unit.Timing.Duration.Microseconds > 0);
        Assert.All(project.Lyrics.Lines.SelectMany(line => line.Units), unit => Assert.False(unit.IsConfirmed));
        var repository = new JsonProjectRepository();
        repository.SaveAsync(context.Root, project).GetAwaiter().GetResult();
        var window = context.Open(repository.LoadAsync(context.Root).GetAwaiter().GetResult());
        Assert.Contains("0/4", Progress(window));
        Assert.Contains("0/2 句完成", Progress(window));
        Assert.Equal(project.Lyrics.Lines[0].Id, Current(window).LineId);
        Assert.Equal(0, Current(window).ActiveUnitIndex);
        Assert.False(ContainsGreenGlyphs(Current(window)));
        Tap(window, 1);
        Assert.Contains("风", Hint(window));
        Assert.Contains("0/4", Progress(window));
    });

    [Fact]
    public Task FailedFirstTapDoesNotCarryTheOldResumePlanIntoExplicitRerecord() => RunSta(context =>
    {
        var project = Confirm(Confirm(Project("春风\n明天"), 0, 0, 1, 2), 0, 1, 2, 3);
        var window = context.Open(project);
        var offset = (TextBox)window.FindName("InputOffsetText");
        offset.Text = "invalid";
        Assert.False((bool)Call(window, "RecordMarker", At(5))!);
        Assert.Empty(Markers(window));
        Call(window, "BrowseRecordingLines", -1);
        Call(window, "RecordFromBrowsedLine_Click", window, new RoutedEventArgs());
        offset.Text = "0";
        Tap(window, 10);
        var plan = (IReadOnlyList<UnitTapTarget>)Field("_unitTapTargets").GetValue(window)!;
        Assert.Equal(project.Lyrics.Lines[0].Units[0].Id, plan[0].UnitId);
        Assert.Equal(4, plan.Count);
        Assert.Equal(project.Lyrics.Lines[0].Id, Current(window).LineId);
    });

    [Fact]
    public Task PreRollCannotDiscardPendingOrUndoableTaps() => RunSta(context =>
    {
        var window = context.Open(Project("春风"));
        Tap(window, 1);
        Call(window, "PreRollButton_Click", window, new RoutedEventArgs());
        Assert.Single(Markers(window));
        Assert.False(Playback(window).IsPlaying);
        Call(window, "UndoLastMarker");
        Call(window, "PreRollButton_Click", window, new RoutedEventArgs());
        Call(window, "RedoMarkerButton_Click", window, new RoutedEventArgs());
        Assert.Equal(new[] { At(1) }, Markers(window));
        Assert.False(Playback(window).IsPlaying);
    });

    private static KaraokeProject Project(string lyrics) => KaraokeProject.Create("打轴进度回归", DateTimeOffset.UtcNow) with
    { Lyrics = new LyricImportService().Parse(lyrics, ".txt", LyricLanguage.Chinese) };

    private static KaraokeProject Confirm(KaraokeProject project, int lineIndex, int unitIndex, double start, double end)
    {
        var lines = project.Lyrics.Lines.ToArray();
        var line = lines[lineIndex]; var units = line.Units.ToArray();
        units[unitIndex] = units[unitIndex] with { Timing = new TimeRange(At(start), At(end)), RawInputTime = At(start), IsConfirmed = true };
        // An untimed trailing unit is a zero-length placeholder after the confirmed prefix,
        // keeping the saved fixture valid without falsely marking the whole line complete.
        for (int index = 1; index < units.Length; index++)
        {
            if (!units[index].IsConfirmed && units[index].Timing.Duration.Microseconds == 0)
            { units[index] = units[index] with { Timing = new TimeRange(units[index - 1].Timing.End, units[index - 1].Timing.End) }; }
        }
        lines[lineIndex] = line with { Units = units, Timing = new TimeRange(units.Min(unit => unit.Timing.Start), units.Max(unit => unit.Timing.End)) };
        return project with { Lyrics = project.Lyrics with { Lines = lines } };
    }

    private static void AssertSavedUnitUnchanged(LyricUnit before, LyricUnit after)
    {
        Assert.Equal(before.Id, after.Id); Assert.Equal(before.Timing, after.Timing);
        Assert.Equal(before.RawInputTime, after.RawInputTime); Assert.Equal(before.IsConfirmed, after.IsConfirmed);
    }

    private static bool ContainsGreenGlyphs(RecordingLyricLine line)
    {
        line.Measure(new Size(300, 80)); line.Arrange(new Rect(0, 0, 300, 80)); line.UpdateLayout();
        var bitmap = new RenderTargetBitmap(300, 80, 96, 96, PixelFormats.Pbgra32); bitmap.Render(line);
        var pixels = new byte[300 * 80 * 4]; bitmap.CopyPixels(pixels, 300 * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            // LightGreen text has a stronger green channel than red or blue; the row's blue
            // background/borders, white text and gold pending markers cannot satisfy this.
            if (pixels[i + 3] > 200 && pixels[i + 1] > pixels[i] + 30 && pixels[i + 1] > pixels[i + 2] + 30) { return true; }
        }
        return false;
    }

    private static void SaveOptionalSnapshot(MainWindow window)
    {
        string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) { return; }
        Call(window, "RefreshWorkspaceDisplay"); Call(window, "RefreshMarkerList");
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1440, 940)); content.Arrange(new Rect(0, 0, 1440, 940)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1440, 940, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, "recording-restored-progress.png")); encoder.Save(output);
    }

    private static async Task RunSta(Action<TestContext> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { using (var context = new TestContext()) { test(context); } completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class TestContext : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "KaraokeFilmMaker.ProgressTests", Guid.NewGuid().ToString("N"));
        private readonly List<MainWindow> _windows = [];
        public MainWindow Open(KaraokeProject project)
        {
            var window = new MainWindow(); _windows.Add(window);
            ((IAudioPlaybackSession)Field("_playback").GetValue(window)!).DisposeAsync().AsTask().GetAwaiter().GetResult();
            Field("_playback").SetValue(window, new StubPlayback());
            Field("_workspace").SetValue(window, new WorkspaceSession(Root, project));
            Call(window, "RefreshLyricRows"); Call(window, "RefreshVideoRows");
            ((TabControl)window.FindName("EditorTabs")).SelectedIndex = 1;
            Call(window, "RefreshTapHint", At(0));
            return window;
        }
        public void Close(MainWindow window)
        { Field("_allowClose").SetValue(window, true); window.Close(); _windows.Remove(window); }
        public void Dispose()
        {
            foreach (var window in _windows.ToArray()) { Close(window); }
            if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); }
        }
    }

    private static ProjectTime At(double seconds) => ProjectTime.FromSeconds(seconds);
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static object? Call(MainWindow window, string name, params object?[] args) => typeof(MainWindow)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static WorkspaceSession Session(MainWindow window) => (WorkspaceSession)Field("_workspace").GetValue(window)!;
    private static StubPlayback Playback(MainWindow window) => (StubPlayback)Field("_playback").GetValue(window)!;
    private static List<ProjectTime> Markers(MainWindow window) => (List<ProjectTime>)Field("_markers").GetValue(window)!;
    private static RecordingLyricLine Current(MainWindow window) => (RecordingLyricLine)window.FindName("CurrentRecordingLine");
    private static string Hint(MainWindow window) => ((TextBlock)window.FindName("NextTapText")).Text;
    private static string Progress(MainWindow window) => ((TextBlock)window.FindName("RecordingProgressText")).Text;
    private static void Tap(MainWindow window, double seconds)
    { Assert.True((bool)Call(window, "RecordMarker", At(seconds))!); Call(window, "RefreshMarkerList"); }

    private sealed class StubPlayback : IAudioPlaybackSession
    {
        public ProjectTime Position { get; set; }
        public bool IsPlaying { get; private set; }
        public bool IsLoaded => true;
        public ProjectDuration Duration => ProjectDuration.FromSeconds(30);
        public string? SourcePath => null;
        public Task LoadAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PlayAsync(CancellationToken cancellationToken = default) { IsPlaying = true; return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken = default) { IsPlaying = false; return Task.CompletedTask; }
        public Task SeekAsync(ProjectTime position, CancellationToken cancellationToken = default) { Position = position; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
