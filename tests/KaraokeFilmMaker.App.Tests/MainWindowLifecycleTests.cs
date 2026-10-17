using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Tests;

public sealed class MainWindowLifecycleTests
{
    [Theory]
    [InlineData("unchanged", true, 0)]
    [InlineData("save", true, 1)]
    [InlineData("discard", true, 1)]
    [InlineData("cancel", false, 1)]
    [InlineData("invalid-cancel", false, 1)]
    [InlineData("invalid-close", true, 1)]
    [InlineData("pending-cancel", false, 1)]
    [InlineData("pending-close", true, 1)]
    [InlineData("undone-cancel", false, 1)]
    [InlineData("undone-close", true, 1)]
    [InlineData("quick-save", true, 1)]
    [InlineData("quick-discard", true, 1)]
    [InlineData("quick-cancel", false, 1)]
    [InlineData("recovery-save", true, 1)]
    [InlineData("quick-save-failure", false, 2)]
    [InlineData("source-save", true, 1)]
    [InlineData("source-discard", true, 1)]
    [InlineData("source-cancel", false, 1)]
    [InlineData("source-invalid", false, 1)]
    [InlineData("source-save-failure", false, 2)]
    public async Task CloseRunsOutsideClosingAndPreservesSaveChoices(string scenario, bool shouldClose, int promptCount)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "Liraoke.CloseTests", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            FileStream? lockedProject = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                Directory.CreateDirectory(root);
                window = new MainWindow();
                bool closeCallOnStack = false;
                var answers = new Queue<MessageBoxResult>();
                var prompts = new List<string>();
                Func<Window, string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult, MessageBoxResult> prompt =
                    (_, message, _, _, _, _) =>
                    {
                        Assert.False(closeCallOnStack);
                        prompts.Add(message);
                        return answers.Dequeue();
                    };
                Field("_closePrompt").SetValue(window, prompt);
                bool closed = false;
                window.Closed += (_, _) => closed = true;
                Field("_workspace").SetValue(window, new WorkspaceSession(root,
                    KaraokeProject.Create("关闭回归", DateTimeOffset.UtcNow) with
                    { Lyrics = new LyricImportService().Parse("春风", ".txt", LyricLanguage.Chinese) }));
                foreach (string method in new[] { "RefreshLyricRows", "RefreshVideoRows", "RefreshSubtitleStyleFields", "RefreshExportPreset",
                    "ApplyLyricRowsToProject", "ApplyVideoRowsToProject", "ApplySubtitleStyleFields", "ApplyExportPreset" })
                {
                    Method(method).Invoke(window, null);
                }
                var original = ((WorkspaceSession)Field("_workspace").GetValue(window)!).Project;
                string originalContent = ProjectRecoveryStore.Content(original);
                Field("_lastExplicitContent").SetValue(window, originalContent);
                var repository = new JsonProjectRepository();
                Task.Run(() => repository.SaveAsync(root, original)).GetAwaiter().GetResult();

                var fontSize = (TextBox)window.FindName("PrimaryFontSizeText");
                string originalFontSize = fontSize.Text;
                if (scenario is "save" or "discard" or "cancel" or "recovery-save") { fontSize.Text = "72"; }
                if (scenario.StartsWith("invalid")) { fontSize.Text = "invalid"; }
                var markers = (List<ProjectTime>)Field("_markers").GetValue(window)!;
                if (scenario.StartsWith("pending")) { markers.Add(ProjectTime.FromSeconds(1)); }
                var undone = (Stack<(ProjectTime Time, ProjectTime Raw)>)Field("_undoneMarkers").GetValue(window)!;
                if (scenario.StartsWith("undone")) { undone.Push((ProjectTime.FromSeconds(1), ProjectTime.FromSeconds(1))); }
                if (scenario.StartsWith("quick"))
                {
                    Method("RecordingUnitSelected").Invoke(window, [original.Lyrics.Lines[0].Id, original.Lyrics.Lines[0].Units[0].Id]);
                    ((TextBox)window.FindName("QuickEditText")).Text = "清";
                    ((TextBox)window.FindName("QuickEditReading")).Text = "qing";
                }
                object? sourceRow = null;
                if (scenario.StartsWith("source"))
                {
                    var beginEdit = (Task)Method("BeginSourceLyricEditAsync").Invoke(window, [original.Lyrics.Lines[0].Id])!;
                    PumpUntil(() => beginEdit.IsCompleted);
                    beginEdit.GetAwaiter().GetResult();
                    sourceRow = ((DataGrid)window.FindName("LyricsGrid")).Items[0];
                    sourceRow.GetType().GetProperty("SourceEditText")!.SetValue(sourceRow,
                        scenario == "source-invalid" ? " " : "春雨");
                    Assert.True((bool)Method("HasSourceLyricDraft").Invoke(window, null)!);
                    // Drafts must not leak into autosave or close's committed-row synchronization.
                    Method("ApplyLyricRowsToProject").Invoke(window, null);
                    Assert.Equal(originalContent, ProjectRecoveryStore.Content(((WorkspaceSession)Field("_workspace").GetValue(window)!).Project));
                }
                if (scenario == "recovery-save")
                {
                    Method("ApplySubtitleStyleFields").Invoke(window, null);
                    var recovered = ((WorkspaceSession)Field("_workspace").GetValue(window)!).Project;
                    Task.Run(() => new ProjectRecoveryStore().SaveAsync(root, recovered)).GetAwaiter().GetResult();
                    Field("_lastRecoveryContent").SetValue(window, ProjectRecoveryStore.Content(recovered));
                }
                answers.Enqueue(scenario switch
                {
                    "discard" or "quick-discard" or "source-discard" or "invalid-cancel" or "pending-cancel" or "undone-cancel" => MessageBoxResult.No,
                    "cancel" or "quick-cancel" or "source-cancel" => MessageBoxResult.Cancel,
                    _ => MessageBoxResult.Yes,
                });
                if (scenario is "quick-save-failure" or "source-save-failure")
                {
                    // Deny replacement of this test-owned project to exercise a real disk-write failure.
                    lockedProject = File.Open(Path.Combine(root, "project.kfp"), FileMode.Open, FileAccess.Read, FileShare.Read);
                    answers.Enqueue(MessageBoxResult.No);
                }

                closeCallOnStack = true;
                window.Close();
                closeCallOnStack = false;
                Assert.False(closed);
                Assert.False(window.IsEnabled);
                Assert.Empty(prompts);
                Assert.True((bool)Field("_closingRequested").GetValue(window)!);
                // A second title-bar close while the first is queued must not start another save/prompt.
                closeCallOnStack = true;
                window.Close();
                closeCallOnStack = false;
                PumpUntil(() => !(bool)Field("_closingRequested").GetValue(window)!);
                lockedProject?.Dispose(); lockedProject = null;
                Assert.Equal(shouldClose, closed);
                Assert.Equal(promptCount, prompts.Count);
                Assert.DoesNotContain(prompts, text => text.Contains("窗口关闭期间"));

                var saved = Task.Run(() => repository.LoadAsync(root)).GetAwaiter().GetResult();
                if (scenario is "save" or "recovery-save") { Assert.Equal(72d, saved.SubtitleStyle.PrimaryFontSize); }
                else if (scenario == "quick-save")
                {
                    Assert.Equal("清风", saved.Lyrics.Lines[0].PrimaryText);
                    Assert.Equal("qing", saved.Lyrics.Lines[0].Units[0].Reading);
                }
                else if (scenario == "source-save")
                {
                    Assert.Equal("春雨", saved.Lyrics.Lines[0].PrimaryText);
                    Assert.NotEmpty(saved.Lyrics.Lines[0].Units);
                    Assert.NotNull(saved.Lyrics.Lines[0].Presentation);
                }
                else { Assert.Equal(originalContent, ProjectRecoveryStore.Content(saved)); }
                if (scenario.StartsWith("source"))
                { Assert.Contains("歌词获取 / 预览", prompts[0]); }
                if (scenario == "discard")
                {
                    var recovery = Task.Run(() => new JsonProjectRepository("project.recovery.kfp").LoadAsync(root)).GetAwaiter().GetResult();
                    Assert.Equal(72d, recovery.SubtitleStyle.PrimaryFontSize);
                }
                if (scenario == "source-discard")
                {
                    var recovery = Task.Run(() => new JsonProjectRepository("project.recovery.kfp").LoadAsync(root)).GetAwaiter().GetResult();
                    Assert.Equal(originalContent, ProjectRecoveryStore.Content(recovery));
                }
                if (scenario.StartsWith("invalid") || scenario is "quick-save-failure" or "source-save-failure")
                {
                    if (scenario.StartsWith("invalid")) { Assert.Contains("主歌词字号", Assert.Single(prompts)); }
                    Assert.True(File.Exists(Path.Combine(root, "logs", "application.log")));
                }
                else { Assert.False(File.Exists(Path.Combine(root, "logs", "application.log"))); }
                if (!shouldClose)
                {
                    Assert.True(window.IsEnabled);
                    Assert.False((bool)Field("_allowClose").GetValue(window)!);
                    Assert.True(((DispatcherTimer)Field("_recoveryTimer").GetValue(window)!).IsEnabled);
                    if (scenario == "pending-cancel") { Assert.Single(markers); }
                    if (scenario == "undone-cancel") { Assert.Single(undone); }
                    if (scenario == "quick-cancel")
                    {
                        Assert.Equal("清", ((TextBox)window.FindName("QuickEditText")).Text);
                        Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("QuickEditPanel")).Visibility);
                    }
                    if (scenario is "source-cancel" or "source-invalid")
                    {
                        Assert.True((bool)Method("HasSourceLyricDraft").Invoke(window, null)!);
                        Assert.True((bool)sourceRow!.GetType().GetProperty("IsSourceEditing")!.GetValue(sourceRow)!);
                        Assert.Equal(scenario == "source-invalid" ? " " : "春雨",
                            sourceRow.GetType().GetProperty("SourceEditText")!.GetValue(sourceRow));
                    }
                    if (scenario == "source-save-failure")
                    {
                        var previous = Assert.Single((Stack<KaraokeProject>)Field("_undoProjects").GetValue(window)!);
                        Assert.Equal(originalContent, ProjectRecoveryStore.Content(previous));
                    }
                    // Cancel must leave the window usable for a later successful close.
                    fontSize.Text = originalFontSize;
                    markers.Clear();
                    undone.Clear();
                    ((FrameworkElement)window.FindName("QuickEditPanel")).Visibility = Visibility.Collapsed;
                    if (scenario.StartsWith("source")) { Method("CancelSourceLyricEdit").Invoke(window, null); }
                    if (scenario is "quick-save-failure" or "source-save-failure") { answers.Enqueue(MessageBoxResult.Yes); }
                    window.Close();
                    PumpUntil(() => !(bool)Field("_closingRequested").GetValue(window)!);
                    Assert.True(closed);
                    Assert.Equal(promptCount + (scenario is "quick-save-failure" or "source-save-failure" ? 1 : 0), prompts.Count);
                    if (scenario == "quick-save-failure")
                    {
                        var retried = Task.Run(() => repository.LoadAsync(root)).GetAwaiter().GetResult();
                        Assert.Equal("清风", retried.Lyrics.Lines[0].PrimaryText);
                        Assert.Equal("qing", retried.Lyrics.Lines[0].Units[0].Reading);
                    }
                    if (scenario == "source-save-failure")
                    {
                        var retried = Task.Run(() => repository.LoadAsync(root)).GetAwaiter().GetResult();
                        Assert.Equal("春雨", retried.Lyrics.Lines[0].PrimaryText);
                    }
                }
                finished.TrySetResult();
            }
            catch (Exception exception) { finished.TrySetException(exception); }
            finally
            {
                lockedProject?.Dispose();
                if (window is not null)
                {
                    Field("_allowClose").SetValue(window, true);
                    window.Close();
                }
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void PumpUntil(Func<bool> completed)
    {
        var frame = new DispatcherFrame();
        var timeout = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (completed() || DateTime.UtcNow >= timeout) { frame.Continue = false; } };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(completed(), "The close workflow did not finish.");
    }

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
}
