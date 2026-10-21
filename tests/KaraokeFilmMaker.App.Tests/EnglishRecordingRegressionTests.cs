using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Tests;

public sealed class EnglishRecordingRegressionTests
{
    [Fact]
    public Task EnterEndsSurviveReopenAndAllowAdjacentOverlapButNotThirdRow() => RunSta(context =>
    {
        var project = KaraokeProject.Create("Overlap", DateTimeOffset.UnixEpoch) with
        { Lyrics = new LyricImportService().Parse("Come home\nWith me\nSing now", ".txt", LyricLanguage.English) };
        var window = context.Open(project);
        Press(window, Key.Space, 1); Press(window, Key.Space, 2); Press(window, Key.Enter, 8);
        var first = Session(window).Project;
        Assert.Equal(At(8), first.Lyrics.Lines[0].ExplicitEndTime);
        var repository = new JsonProjectRepository();
        repository.SaveAsync(context.Root, first).GetAwaiter().GetResult();
        context.Close(window);
        window = context.Open(repository.LoadAsync(context.Root).GetAwaiter().GetResult());
        Playback(window).PauseAsync().GetAwaiter().GetResult();
        Playback(window).SeekAsync(At(5)).GetAwaiter().GetResult();
        Playback(window).PlayAsync().GetAwaiter().GetResult();
        Press(window, Key.Space, 6); Press(window, Key.Space, 7); Press(window, Key.Enter, 10);
        var second = Session(window).Project;
        Assert.Empty(Markers(window));
        Assert.Equal(At(6), second.Lyrics.Lines[1].Timing.Start);
        Assert.Equal(first.Lyrics.Lines[0].Timing, second.Lyrics.Lines[0].Timing);
        Press(window, Key.Space, 7.5);
        Assert.Empty(Markers(window));
        Assert.Contains("只允许相邻两句重叠", ((TextBlock)window.FindName("StatusText")).Text);
        Press(window, Key.Space, 8); Press(window, Key.Space, 9); Press(window, Key.Enter, 11);
        Assert.Empty(Markers(window));
        Assert.All(Session(window).Project.Lyrics.Lines.SelectMany(l => l.Units), u => Assert.True(u.IsConfirmed));
        Assert.Equal(At(8), Session(window).Project.Lyrics.Lines[2].Timing.Start);
    });

    [Fact]
    public Task PersistedEnglishContextWarningDoesNotBlockWordShortcutsOrRebuildSavedAnalysis() => RunSta(context =>
    {
        var original = EnglishProject();
        var repository = new JsonProjectRepository();
        repository.SaveAsync(context.Root, original).GetAwaiter().GetResult();
        var loaded = repository.LoadAsync(context.Root).GetAwaiter().GetResult();
        AssertSavedAnalysis(original.Lyrics, loaded.Lyrics);
        var window = context.Open(loaded);

        Assert.Equal(0, Current(window).ActiveUnitIndex);
        Assert.Contains("下一单元", Hint(window));
        Assert.Contains("We", Hint(window));
        Assert.Empty(Markers(window));
        Assert.Equal(Visibility.Collapsed, WarningPanel(window).Visibility);
        Call(window, "BrowseRecordingLines", 7);
        Assert.Equal(loaded.Lyrics.Lines[7].Id, Current(window).LineId);
        Assert.Equal(Visibility.Collapsed, WarningPanel(window).Visibility);
        Assert.Contains("We", Hint(window)); // Browsing never moves the actual recording cursor.

        Playback(window).PlayAsync().GetAwaiter().GetResult();
        int total = loaded.Lyrics.Lines.Sum(line => line.Units.Count);
        for (int index = 0; index < total; index++)
        {
            Press(window, Key.Space, index + 1);
            Assert.Equal(index + 1, Markers(window).Count);
            if (index == 0)
            {
                Assert.Equal(1, Current(window).ActiveUnitIndex);
                Assert.Contains("sing", Hint(window));
            }
            if (index == 20)
            {
                Assert.Equal(loaded.Lyrics.Lines[7].Id, Current(window).LineId);
                Assert.Equal(0, Current(window).ActiveUnitIndex);
                Assert.Contains("Silver", Hint(window));
                Assert.Contains("建议确认（可打轴）", ((TextBlock)window.FindName("CurrentRecordingLabel")).Text);
                Assert.Equal(Visibility.Visible, WarningPanel(window).Visibility);
                Assert.True(Playback(window).IsPlaying);
                Assert.False(string.IsNullOrWhiteSpace(((TextBlock)window.FindName("RecordingLanguageWarningText")).Text));
                SaveOptionalSnapshot(window);
                Call(window, "DismissRecordingLanguageWarning_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, WarningPanel(window).Visibility);
                Assert.True(Playback(window).IsPlaying);
                Field("_lastTapHint").SetValue(window, null);
                Call(window, "RefreshTapHint", At(index + 1));
                Assert.Equal(Visibility.Collapsed, WarningPanel(window).Visibility);
            }
            if (index is 21 or 22)
            {
                Assert.Equal(Visibility.Collapsed, WarningPanel(window).Visibility);
                Assert.Equal(loaded.Lyrics.Lines[7].Id, Current(window).LineId);
            }
        }

        Press(window, Key.Enter, total + 1);
        var result = Session(window).Project;
        Assert.Empty(Markers(window));
        Assert.All(result.Lyrics.Lines.SelectMany(line => line.Units), unit => Assert.True(unit.IsConfirmed));
        Assert.Equal(Enumerable.Range(1, total).Select(value => At(value)),
            result.Lyrics.Lines.SelectMany(line => line.Units).Select(unit => unit.Timing.Start));
        Assert.Equal(At(total + 1), result.Lyrics.Lines[^1].Units[^1].Timing.End);
        Assert.Contains("全部打轴已完成", Hint(window));
        AssertSavedAnalysis(loaded.Lyrics, result.Lyrics);
        for (int index = 0; index < loaded.Lyrics.Lines.Count; index++)
        { Assert.Same(loaded.Lyrics.Lines[index].Presentation, result.Lyrics.Lines[index].Presentation); }
        Assert.True(Assert.Single(result.Lyrics.Lines[7].LanguageSpans!).NeedsReview);
    });

    [Fact]
    public Task SavedEnglishProgressResumesThroughContextLineWithoutChangingEarlierWords() => RunSta(context =>
    {
        var original = EnglishProject();
        var lines = original.Lyrics.Lines.ToArray();
        for (int lineIndex = 0; lineIndex < 7; lineIndex++)
        {
            var units = lines[lineIndex].Units.Select((unit, index) => unit with
            {
                Timing = new(At(lineIndex * 4 + index + 1), At(lineIndex * 4 + index + 2)),
                RawInputTime = At(lineIndex * 4 + index + 1), IsConfirmed = true,
            }).ToArray();
            lines[lineIndex] = lines[lineIndex] with { Units = units, Timing = new(units[0].Timing.Start, units[^1].Timing.End) };
        }
        original = original with { Lyrics = original.Lyrics with { Lines = lines } };
        var repository = new JsonProjectRepository();
        repository.SaveAsync(context.Root, original).GetAwaiter().GetResult();
        var loaded = repository.LoadAsync(context.Root).GetAwaiter().GetResult();
        var window = context.Open(loaded);
        Assert.Equal(loaded.Lyrics.Lines[7].Id, Current(window).LineId);
        Assert.Equal(0, Current(window).ActiveUnitIndex);
        Assert.Contains("21/27", ((TextBlock)window.FindName("RecordingProgressText")).Text);
        Assert.Contains("Silver", Hint(window));

        Press(window, Key.Space, 30); Press(window, Key.Space, 31); Press(window, Key.Space, 32);
        Press(window, Key.Enter, 33);
        var firstSave = Session(window).Project;
        Assert.Empty(Markers(window));
        Assert.Equal(firstSave.Lyrics.Lines[8].Id, Current(window).LineId);
        AssertEarlierTimingUnchanged(original.Lyrics, firstSave.Lyrics, 7);
        AssertSavedAnalysis(original.Lyrics, firstSave.Lyrics);
        Assert.Equal(new[] { At(30), At(31), At(32) }, firstSave.Lyrics.Lines[7].Units.Select(unit => unit.Timing.Start));

        repository.SaveAsync(context.Root, firstSave).GetAwaiter().GetResult();
        context.Close(window);
        var reopenedProject = repository.LoadAsync(context.Root).GetAwaiter().GetResult();
        var reopened = context.Open(reopenedProject);
        Assert.Empty(Markers(reopened));
        Assert.Equal(firstSave.Lyrics.Lines[8].Id, Current(reopened).LineId);
        Assert.Contains("24/27", ((TextBlock)reopened.FindName("RecordingProgressText")).Text);
        AssertEarlierTimingUnchanged(firstSave.Lyrics, reopenedProject.Lyrics, 8);
        AssertSavedAnalysis(firstSave.Lyrics, reopenedProject.Lyrics);

        Press(reopened, Key.Space, 34); Press(reopened, Key.Space, 35); Press(reopened, Key.Space, 36);
        Press(reopened, Key.Enter, 37);
        var completed = Session(reopened).Project;
        AssertEarlierTimingUnchanged(firstSave.Lyrics, completed.Lyrics, 8);
        AssertSavedAnalysis(firstSave.Lyrics, completed.Lyrics);
        Assert.All(completed.Lyrics.Lines.SelectMany(line => line.Units), unit => Assert.True(unit.IsConfirmed));
        Assert.Contains("全部打轴已完成", Hint(reopened));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task UnknownLanguageWithValidSavedUnitsWarnsButAllowsIgnoringAndRecording(bool useHostFallback) => RunSta(context =>
    {
        var project = UnresolvedProject(japanese: false);
        var line = project.Lyrics.Lines[0];
        if (useHostFallback)
        {
            line = line with { Presentation = null };
            project = project with { Lyrics = project.Lyrics with { Lines = [line] } };
        }
        var window = context.Open(project);
        Assert.Equal(Visibility.Visible, WarningPanel(window).Visibility);
        Assert.Contains("下一单元", Hint(window));
        Press(window, Key.Space, 1);
        Assert.Single(Markers(window));
        Assert.Equal(1, Current(window).ActiveUnitIndex);
        Call(window, "DismissRecordingLanguageWarning_Click", window, new RoutedEventArgs());
        Assert.Equal(Visibility.Collapsed, WarningPanel(window).Visibility);
        Press(window, Key.Space, 2); Press(window, Key.Enter, 3);
        Assert.Empty(Markers(window));
        Assert.All(Session(window).Project.Lyrics.Lines[0].Units, unit => Assert.True(unit.IsConfirmed));
        Assert.Same(line.Presentation, Session(window).Project.Lyrics.Lines[0].Presentation);
        AssertSavedAnalysis(project.Lyrics, Session(window).Project.Lyrics);
    });

    [Theory]
    [InlineData("JapaneseMissingReading")]
    [InlineData("UnknownMismatchedSnapshot")]
    [InlineData("UnknownMissingUnits")]
    public Task MissingRecordingPrerequisitesStillBlockSpaceRecording(string problem) => RunSta(context =>
    {
        bool japanese = problem == "JapaneseMissingReading";
        var project = UnresolvedProject(japanese);
        var line = project.Lyrics.Lines[0];
        if (problem == "UnknownMismatchedSnapshot")
        { line = line with { Presentation = line.Presentation! with { SourceText = "Different source" } }; }
        if (problem == "UnknownMissingUnits")
        { line = line with { Units = [], Presentation = new(line.PrimaryText, [], []) }; }
        project = project with { Lyrics = project.Lyrics with { Lines = [line] } };
        var window = context.Open(project);
        Press(window, Key.Space, 1);
        Assert.Empty(Markers(window));
        Assert.Equal(-1, Current(window).ActiveUnitIndex);
        Assert.False(string.IsNullOrWhiteSpace(((TextBlock)window.FindName("StatusText")).Text));
        if (japanese) { Assert.Contains("日语", ((TextBlock)window.FindName("StatusText")).Text); }
        Assert.Same(line.Presentation, Session(window).Project.Lyrics.Lines[0].Presentation);
        AssertSavedAnalysis(project.Lyrics, Session(window).Project.Lyrics);
    });

    private static KaraokeProject UnresolvedProject(bool japanese)
    {
        string text = japanese ? "駅" : "Unfamiliar syllables";
        string language = japanese ? LanguageIds.Japanese : LanguageIds.Unknown;
        string diagnostic = japanese ? "日语读音尚未完整，请填写假名。" : "无法确定语言，请指定片段语言。";
        var line = new LyricImportService().Parse(text, ".txt", japanese ? LyricLanguage.Japanese : LyricLanguage.Unknown).Lines[0];
        line = line with
        {
            Units = line.Units.Select(unit => unit with { LanguageId = language }).ToArray(),
            LanguageSpans = [new(Guid.NewGuid(), new(0, text.Length), language,
                japanese ? LanguageDetectionSource.Context : LanguageDetectionSource.Unknown,
                NeedsReview: true, Diagnostic: diagnostic, DetectionNeedsReview: true)],
        };
        line = line with { Presentation = japanese ? LanguagePresentation.BuildJapanese(line) : LanguagePresentation.BuildPlain(line) };
        return KaraokeProject.Create("Mandatory language checks", DateTimeOffset.UtcNow) with
        { Lyrics = new(LyricLanguage.Auto, [line]) };
    }

    private static KaraokeProject EnglishProject()
    {
        // Synthetic phrases, not lyrics from a user's project. The eighth phrase deliberately
        // lacks the detector's common-word evidence, while the rest establish English context.
        string lyrics = string.Join('\n', Enumerable.Repeat("We sing softly", 7)
            .Append("Silver feathers shimmer").Append("We sing again"));
        var imported = new LyricImportService().Parse(lyrics, ".txt", LyricLanguage.Auto);
        var service = new LanguageAnalysisService(new LanguageRegistry([new EnglishLanguageAdaptor()]));
        var analyzed = service.AnalyzeDocumentAsync(imported).GetAwaiter().GetResult();
        var span = Assert.Single(analyzed.Lines[7].LanguageSpans!);
        Assert.Equal(LanguageIds.English, span.LanguageId);
        Assert.Equal(LanguageDetectionSource.Context, span.Source);
        Assert.True(span.NeedsReview); Assert.True(span.DetectionNeedsReview);
        Assert.All(analyzed.Lines, line => { Assert.Equal(3, line.Units.Count); Assert.NotNull(line.Presentation); });
        return KaraokeProject.Create("English context recording regression", DateTimeOffset.UtcNow) with { Lyrics = analyzed };
    }

    private static void AssertSavedAnalysis(LyricDocument before, LyricDocument after)
    {
        Assert.Equal(before.Lines.Select(line => line.Id), after.Lines.Select(line => line.Id));
        for (int index = 0; index < before.Lines.Count; index++)
        {
            var left = before.Lines[index]; var right = after.Lines[index];
            Assert.Equal(left.PrimaryText, right.PrimaryText);
            Assert.Equal(left.Units.Select(unit => (unit.Id, unit.Reading, unit.LanguageId)),
                right.Units.Select(unit => (unit.Id, unit.Reading, unit.LanguageId)));
            Assert.Equal(JsonSerializer.Serialize(left.LanguageSpans), JsonSerializer.Serialize(right.LanguageSpans));
            Assert.Equal(JsonSerializer.Serialize(left.Presentation), JsonSerializer.Serialize(right.Presentation));
            for (int unit = 0; unit < left.Units.Count; unit++)
            { Assert.Equal(left.Units[unit].DisplayRanges, right.Units[unit].DisplayRanges); }
        }
    }

    private static void AssertEarlierTimingUnchanged(LyricDocument before, LyricDocument after, int lineCount)
    {
        for (int index = 0; index < lineCount; index++)
        {
            Assert.Equal(before.Lines[index].Timing, after.Lines[index].Timing);
            Assert.Equal(before.Lines[index].Units.Select(unit => (unit.Id, unit.Timing, unit.RawInputTime, unit.IsConfirmed)),
                after.Lines[index].Units.Select(unit => (unit.Id, unit.Timing, unit.RawInputTime, unit.IsConfirmed)));
        }
    }

    private static void Press(MainWindow window, Key key, double seconds)
    {
        Playback(window).Position = At(seconds);
        Keyboard.ClearFocus();
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = window.FindName("ProjectTimeline") };
        Call(window, "Window_PreviewKeyDown", window, args);
        Assert.True(args.Handled);
    }

    private static void SaveOptionalSnapshot(MainWindow window)
    {
        string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) { return; }
        Call(window, "RefreshWorkspaceDisplay");
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1440, 940)); content.Arrange(new Rect(0, 0, 1440, 940)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1440, 940, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, "english-context-recording.png")); encoder.Save(output);
    }

    private static async Task RunSta(Action<TestContext> test)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { using (var context = new TestContext()) { test(context); } finished.TrySetResult(); }
            catch (Exception error) { finished.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class TestContext : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "KaraokeFilmMaker.EnglishRecordingTests", Guid.NewGuid().ToString("N"));
        private readonly List<MainWindow> _windows = [];
        public MainWindow Open(KaraokeProject project)
        {
            var window = new MainWindow(); _windows.Add(window);
            ((IAudioPlaybackSession)Field("_playback").GetValue(window)!).DisposeAsync().AsTask().GetAwaiter().GetResult();
            Field("_playback").SetValue(window, new StubPlayback());
            Field("_workspace").SetValue(window, new WorkspaceSession(Root, project));
            Call(window, "RefreshLyricRows"); Call(window, "RefreshVideoRows");
            ((TabControl)window.FindName("EditorTabs")).SelectedIndex = 1;
            ((TextBox)window.FindName("InputOffsetText")).Text = "0";
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

    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private static ProjectTime At(double seconds) => ProjectTime.FromSeconds(seconds);
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static object? Call(MainWindow window, string name, params object?[] arguments) => typeof(MainWindow)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
    private static WorkspaceSession Session(MainWindow window) => (WorkspaceSession)Field("_workspace").GetValue(window)!;
    private static StubPlayback Playback(MainWindow window) => (StubPlayback)Field("_playback").GetValue(window)!;
    private static List<ProjectTime> Markers(MainWindow window) => (List<ProjectTime>)Field("_markers").GetValue(window)!;
    private static RecordingLyricLine Current(MainWindow window) => (RecordingLyricLine)window.FindName("CurrentRecordingLine");
    private static FrameworkElement WarningPanel(MainWindow window) => (FrameworkElement)window.FindName("RecordingLanguageWarningPanel");
    private static string Hint(MainWindow window) => ((TextBlock)window.FindName("NextTapText")).Text;

    private sealed class StubPlayback : IAudioPlaybackSession
    {
        public ProjectTime Position { get; set; }
        public bool IsPlaying { get; private set; }
        public bool IsLoaded => true;
        public ProjectDuration Duration => ProjectDuration.FromSeconds(120);
        public string? SourcePath => null;
        public Task LoadAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PlayAsync(CancellationToken cancellationToken = default) { IsPlaying = true; return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken = default) { IsPlaying = false; return Task.CompletedTask; }
        public Task SeekAsync(ProjectTime position, CancellationToken cancellationToken = default) { Position = position; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
