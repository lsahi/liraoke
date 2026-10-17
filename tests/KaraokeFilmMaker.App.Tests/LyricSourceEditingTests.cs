using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Tests;

public sealed class LyricSourceEditingTests
{
    [Fact]
    public async Task SourceRowUsesSeparateEditableDraftAndCancelDoesNotChangeTheProject() => await Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        var original = fixture.Project;
        var tabs = (TabControl)fixture.Window.FindName("EditorTabs");
        tabs.SelectedItem = fixture.Window.FindName("LyricSourceTab");
        Render((FrameworkElement)fixture.Window.Content, "lyric-source-preview.png");
        var grid = (DataGrid)fixture.Window.FindName("LyricsGrid");
        var container = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0)!;
        Assert.NotNull(container);
        var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = Control.MouseDoubleClickEvent };
        container.RaiseEvent(doubleClick);
        Assert.True(doubleClick.Handled);
        var row = fixture.Row(0);
        Assert.True(Get<bool>(row, "IsSourceEditing"));
        Assert.Equal(original.Lyrics.Lines[0].PrimaryText, Get<string>(row, "SourceEditText"));
        Assert.Same(original, fixture.Project);
        Set(row, "SourceEditText", "I love you");
        Assert.True((bool)fixture.Call("HasSourceLyricDraft")!);
        Assert.Equal(original.Lyrics.Lines[0].PrimaryText, Get<string>(row, "PrimaryText"));
        fixture.Call("ApplyLyricRowsToProject"); // A passive refresh/save projection cannot apply the draft.
        Assert.Equal(ProjectRecoveryStore.Content(original), ProjectRecoveryStore.Content(fixture.Project));
        Assert.Empty(fixture.Undo);

        Render((FrameworkElement)fixture.Window.Content, "lyric-source-inline-edit.png");
        var editor = Descendants<TextBox>(grid).FirstOrDefault(item => item.Name == "SourceLyricTextBox" && item.Visibility == Visibility.Visible);
        Assert.NotNull(editor);
        Assert.False(editor.IsReadOnly);
        Assert.Equal("I love you", editor.Text);
        Assert.True(grid.IsReadOnly); // Time columns stay protected; text uses the custom draft template.

        fixture.Call("CancelSourceLyricEdit");
        Assert.False((bool)fixture.Call("HasSourceLyricDraft")!);
        Assert.False(Get<bool>(fixture.Row(0), "IsSourceEditing"));
        Assert.Equal(ProjectRecoveryStore.Content(original), ProjectRecoveryStore.Content(fixture.Project));
        Assert.Empty(fixture.Undo);
    });

    [Fact]
    public async Task ApplyingSourceTextReanalyzesOnlyTheTargetLineAndCanBeUndone() => await Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        var original = fixture.Project;
        await fixture.CallTask("BeginSourceLyricEditAsync", original.Lyrics.Lines[1].Id);
        Set(fixture.Row(1), "SourceEditText", "I love you");
        bool applied = false;
        await fixture.CallTask("RunUiOperationAsync", (Func<Task>)(async () =>
            applied = await (Task<bool>)fixture.Call("ApplySourceLyricDraftAsync")!), true);

        Assert.True(applied);
        var updated = fixture.Project.Lyrics.Lines[1];
        Assert.Equal(original.Lyrics.Lines[1].Id, updated.Id);
        Assert.Equal("I love you", updated.PrimaryText);
        Assert.Equal(new[] { "I", "love", "you" }, updated.Units.Select(unit => unit.Reading));
        Assert.All(updated.Units, unit => { Assert.False(unit.IsConfirmed); Assert.Null(unit.RawInputTime); Assert.Equal("en", unit.LanguageId); });
        Assert.NotNull(updated.Presentation);
        Assert.Equal(updated.PrimaryText, updated.Presentation.SourceText);
        Assert.Equal(original.Lyrics.Lines[0], fixture.Project.Lyrics.Lines[0]);
        Assert.Equal(original.Lyrics.Lines[2], fixture.Project.Lyrics.Lines[2]);
        Assert.False((bool)fixture.Call("HasSourceLyricDraft")!);
        Assert.Single(fixture.Undo);
        await fixture.CallTask("RestoreHistoryAsync", false);
        Assert.Equal(ProjectRecoveryStore.Content(original), ProjectRecoveryStore.Content(fixture.Project));
        await fixture.CallTask("RestoreHistoryAsync", true);
        Assert.Equal("I love you", fixture.Project.Lyrics.Lines[1].PrimaryText);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContiguousInsertionRequiresConsentClearsFollowingTimingAndFollowsNewLine(bool before) => await Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        var original = fixture.Project;
        int prompts = 0;
        fixture.SetPrompt((message, buttons, fallback) =>
        {
            prompts++;
            Assert.Contains("打轴", message);
            Assert.Equal(MessageBoxButton.OKCancel, buttons);
            Assert.Equal(MessageBoxResult.Cancel, fallback);
            return MessageBoxResult.OK;
        });
        Guid anchor = original.Lyrics.Lines[before ? 1 : 0].Id;
        await fixture.CallTask("InsertSourceLyricAsync", anchor, before, "新的歌词");
        Assert.Equal(1, prompts);
        var lines = fixture.Project.Lyrics.Lines;
        Assert.Equal(4, lines.Count);
        Assert.Equal(original.Lyrics.Lines[0], lines[0]);
        Assert.Equal("新的歌词", lines[1].PrimaryText);
        Assert.All(lines.Skip(1), line =>
        {
            Assert.Equal(ProjectTime.Zero, line.Timing.Start);
            Assert.Equal(line.Timing.Start, line.Timing.End);
            Assert.All(line.Units, unit =>
            {
                Assert.Equal(ProjectTime.Zero, unit.Timing.Start);
                Assert.Equal(unit.Timing.Start, unit.Timing.End);
                Assert.False(unit.IsConfirmed);
                Assert.Null(unit.RawInputTime);
            });
        });
        for (int index = 1; index < original.Lyrics.Lines.Count; index++)
        {
            var oldLine = original.Lyrics.Lines[index];
            var kept = lines[index + 1];
            Assert.Equal(oldLine.Id, kept.Id);
            Assert.Equal(oldLine.PrimaryText, kept.PrimaryText);
            Assert.Same(oldLine.Presentation, kept.Presentation);
            Assert.Same(oldLine.LanguageSpans, kept.LanguageSpans);
            Assert.Equal(oldLine.Units.Select(unit => (unit.Id, unit.Reading, unit.Romanization, unit.LanguageId)),
                kept.Units.Select(unit => (unit.Id, unit.Reading, unit.Romanization, unit.LanguageId)));
            Assert.All(oldLine.Units.Zip(kept.Units), pair => Assert.Equal(pair.First.DisplayRanges, pair.Second.DisplayRanges));
        }
        Assert.Equal(lines[1].Id, Field("_browsedRecordingLineId").GetValue(fixture.Window));
        ((TabControl)fixture.Window.FindName("EditorTabs")).SelectedItem = fixture.Window.FindName("RecordingTab");
        fixture.Call("RefreshTapHint", ProjectTime.Zero);
        Assert.Equal(lines[1].Id, ((RecordingLyricLine)fixture.Window.FindName("CurrentRecordingLine")).LineId);
        Assert.Single(fixture.Undo);

        string edited = ProjectRecoveryStore.Content(fixture.Project);
        var repository = new JsonProjectRepository();
        await repository.SaveAsync(fixture.Root, fixture.Project);
        var reopened = await repository.LoadAsync(fixture.Root);
        Assert.Equal(edited, ProjectRecoveryStore.Content(reopened));
        await fixture.CallTask("RestoreHistoryAsync", false);
        Assert.Equal(ProjectRecoveryStore.Content(original), ProjectRecoveryStore.Content(fixture.Project));
        await fixture.CallTask("RestoreHistoryAsync", true);
        Assert.Equal(edited, ProjectRecoveryStore.Content(fixture.Project));
    });

    [Fact]
    public async Task CancellingDestructiveInsertionLeavesProjectHistoryAndBrowsePositionUntouched() => await Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        var original = fixture.Project;
        var browsed = original.Lyrics.Lines[2].Id;
        Field("_browsedRecordingLineId").SetValue(fixture.Window, browsed);
        int prompts = 0;
        fixture.SetPrompt((_, _, _) => { prompts++; return MessageBoxResult.Cancel; });
        await fixture.CallTask("InsertSourceLyricAsync", original.Lyrics.Lines[1].Id, true, "不要插入");
        Assert.Equal(1, prompts);
        Assert.Equal(ProjectRecoveryStore.Content(original), ProjectRecoveryStore.Content(fixture.Project));
        Assert.Empty(fixture.Undo);
        Assert.Empty((IEnumerable)Field("_redoProjects").GetValue(fixture.Window)!);
        Assert.Equal(browsed, Field("_browsedRecordingLineId").GetValue(fixture.Window));
    });

    [Fact]
    public async Task InsertionIntoExistingGapDoesNotEraseLaterConfirmedTiming() => await Sta(async () =>
    {
        using var fixture = await Fixture.Create(gap: true);
        var original = fixture.Project;
        fixture.SetPrompt((_, _, _) => throw new InvalidOperationException("A genuine empty gap must not need destructive confirmation."));
        await fixture.CallTask("InsertSourceLyricAsync", original.Lyrics.Lines[1].Id, true, "空白处的新句");
        var lines = fixture.Project.Lyrics.Lines;
        Assert.Equal(4, lines.Count);
        Assert.Equal(original.Lyrics.Lines[0], lines[0]);
        Assert.Equal(original.Lyrics.Lines[1], lines[2]);
        Assert.Equal(original.Lyrics.Lines[2], lines[3]);
        Assert.Equal("空白处的新句", lines[1].PrimaryText);
        Assert.All(lines[1].Units, unit => { Assert.False(unit.IsConfirmed); Assert.Null(unit.RawInputTime); });
        Assert.Equal(lines[1].Id, Field("_browsedRecordingLineId").GetValue(fixture.Window));
    });

    [Fact]
    public async Task DirtySourceDraftBlocksSwitchingRowsAndUnrelatedOperationsWithoutLosingText() => await Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        var original = fixture.Project;
        await fixture.CallTask("BeginSourceLyricEditAsync", original.Lyrics.Lines[0].Id);
        Set(fixture.Row(0), "SourceEditText", "尚未应用");
        await fixture.CallTask("BeginSourceLyricEditAsync", original.Lyrics.Lines[1].Id);
        Assert.True(Get<bool>(fixture.Row(0), "IsSourceEditing"));
        Assert.False(Get<bool>(fixture.Row(1), "IsSourceEditing"));
        Assert.Equal("尚未应用", Get<string>(fixture.Row(0), "SourceEditText"));
        bool called = false;
        await fixture.CallTask("RunUiOperationAsync", (Func<Task>)(() => { called = true; return Task.CompletedTask; }), false);
        Assert.False(called);
        Assert.False((bool)fixture.Call("CanLeaveSourceLyricEdit")!);
        Assert.Equal(ProjectRecoveryStore.Content(original), ProjectRecoveryStore.Content(fixture.Project));
        Assert.Empty(fixture.Undo);
    });

    [Theory]
    [InlineData("")]
    [InlineData("第一行\n第二行")]
    public async Task InvalidSourceDraftRemainsAvailableForCorrectionWithoutMutation(string draft) => await Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        var original = fixture.Project;
        fixture.SetPrompt((_, _, _) => throw new InvalidOperationException("Invalid drafts must be rejected before asking to clear any timing."));
        await fixture.CallTask("BeginSourceLyricEditAsync", original.Lyrics.Lines[1].Id);
        Set(fixture.Row(1), "SourceEditText", draft);
        bool applied = await (Task<bool>)fixture.Call("ApplySourceLyricDraftAsync")!;
        Assert.False(applied);
        Assert.True((bool)fixture.Call("HasSourceLyricDraft")!);
        Assert.True(Get<bool>(fixture.Row(1), "IsSourceEditing"));
        Assert.Equal(draft, Get<string>(fixture.Row(1), "SourceEditText"));
        Assert.Equal(ProjectRecoveryStore.Content(original), ProjectRecoveryStore.Content(fixture.Project));
        Assert.Empty(fixture.Undo);
    });

    [Fact]
    public async Task ContextMenuTracksRightClickedRowRatherThanTheOldSelection() => await Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        ((TabControl)fixture.Window.FindName("EditorTabs")).SelectedItem = fixture.Window.FindName("LyricSourceTab");
        Render((FrameworkElement)fixture.Window.Content, "lyric-source-context-row.png");
        var grid = (DataGrid)fixture.Window.FindName("LyricsGrid");
        grid.SelectedIndex = 0;
        var second = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(1)!;
        var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            { RoutedEvent = Mouse.PreviewMouseDownEvent };
        fixture.Call("SourceLyricRow_PreviewMouseRightButtonDown", second, rightClick);
        Assert.Equal(1, grid.SelectedIndex);
        var menu = second.ContextMenu;
        Assert.NotNull(menu);
        menu.PlacementTarget = second;
        Assert.Same(fixture.Row(1), menu.DataContext);
        var entries = menu.Items.OfType<MenuItem>().ToArray();
        Assert.Contains(entries, item => item.Header.ToString()!.Contains("之前"));
        Assert.Contains(entries, item => item.Header.ToString()!.Contains("之后"));
        fixture.Call("EditSourceLyric_Click", entries[0], new RoutedEventArgs());
        Assert.True(Get<bool>(fixture.Row(1), "IsSourceEditing"));
        Assert.False(Get<bool>(fixture.Row(0), "IsSourceEditing"));
    });

    private static T Get<T>(object item, string property) => (T)item.GetType().GetProperty(property)!.GetValue(item)!;
    private static void Set(object item, string property, object? value) => item.GetType().GetProperty(property)!.SetValue(item, value);
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private sealed class Fixture : IDisposable
    {
        public MainWindow Window { get; } = new();
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Liraoke.SourceEditingTests", Guid.NewGuid().ToString("N"));
        public KaraokeProject Project => ((WorkspaceSession)Field("_workspace").GetValue(Window)!).Project;
        public Stack<KaraokeProject> Undo => (Stack<KaraokeProject>)Field("_undoProjects").GetValue(Window)!;
        public object Row(int index) => ((IList)Field("_lyricRows").GetValue(Window)!)[index]!;
        public object? Call(string name, params object?[] args) => Method(name).Invoke(Window, args);
        public Task CallTask(string name, params object?[] args) => (Task)Call(name, args)!;
        public void SetPrompt(Func<string, MessageBoxButton, MessageBoxResult, MessageBoxResult> choice)
        {
            Func<Window, string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult, MessageBoxResult> prompt =
                (_, message, _, buttons, _, fallback) => choice(message, buttons, fallback);
            Field("_lyricSourcePrompt").SetValue(Window, prompt);
        }

        public static async Task<Fixture> Create(bool gap = false)
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.Root);
            fixture.SetPrompt((_, _, _) => MessageBoxResult.OK);
            var service = (LanguageAnalysisService)Field("_languageAnalysis").GetValue(fixture.Window)!;
            var document = new LyricImportService().Parse("[00:01]春风\n[00:04]夏雨\n[00:07]秋叶", ".lrc", LyricLanguage.Chinese);
            document = await service.AnalyzeDocumentAsync(document);
            document = document with { Lines = document.Lines.Select((line, index) => line with
            {
                Timing = gap && index == 0 ? new(line.Timing.Start, ProjectTime.FromSeconds(3)) : line.Timing,
                Units = line.Units.Select((unit, unitIndex) => unit with
                {
                    Timing = gap && index == 0 ? new(ProjectTime.FromSeconds(1 + unitIndex), ProjectTime.FromSeconds(2 + unitIndex)) : unit.Timing,
                    RawInputTime = unit.Timing.Start,
                    IsConfirmed = true,
                }).ToArray(),
            }).ToArray() };
            Field("_workspace").SetValue(fixture.Window, new WorkspaceSession(fixture.Root,
                KaraokeProject.Create("歌词来源编辑回归", DateTimeOffset.UtcNow) with { Lyrics = document }));
            foreach (string name in new[] { "RefreshLyricRows", "RefreshVideoRows", "RefreshSubtitleStyleFields", "RefreshExportPreset", "RefreshMarkerList" })
            { fixture.Call(name); }
            return fixture;
        }

        public void Dispose()
        {
            Field("_allowClose").SetValue(Window, true);
            Window.Close();
            if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); }
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed) { yield return typed; }
            foreach (var descendant in Descendants<T>(child)) { yield return descendant; }
        }
    }

    private static void Render(FrameworkElement content, string name)
    {
        const int width = 1600, height = 1000;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) { return; }
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }

    private static async Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var task = action();
                var frame = new DispatcherFrame();
                var timeout = DateTime.UtcNow.AddSeconds(45);
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow >= timeout) { frame.Continue = false; } };
                timer.Start();
                try { Dispatcher.PushFrame(frame); }
                finally { timer.Stop(); }
                Assert.True(task.IsCompleted, "The source editing workflow did not finish.");
                task.GetAwaiter().GetResult();
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(50));
    }
}
