using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Tests;

public sealed class RecordingBrowseTests
{
    [Fact]
    public async Task WholeSongBrowsingIsIndependentOfTapsAndCanEditDistantLyrics()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            string root = Path.Combine(Path.GetTempPath(), "KaraokeFilmMaker.BrowseTests", Guid.NewGuid().ToString("N"));
            try
            {
                window = new MainWindow();
                ((IAudioPlaybackSession)Field("_playback").GetValue(window)!).DisposeAsync().AsTask().GetAwaiter().GetResult();
                var playback = new Playback(); Field("_playback").SetValue(window, playback);
                var document = new LyricImportService().Parse("春风\n夏雨\n间奏\n秋叶\n冬雪\n明月", ".txt", LyricLanguage.Chinese);
                document = document with { Lines = document.Lines.Select((line, i) => i == 2 ? line with { IsInstrumental = true, Units = [] } : line).ToArray() };
                var project = KaraokeProject.Create("浏览测试", DateTimeOffset.UtcNow) with { Lyrics = document };
                Field("_workspace").SetValue(window, new WorkspaceSession(root, project));
                Call("RefreshLyricRows");
                var tabs = (TabControl)window.FindName("EditorTabs"); tabs.SelectedIndex = 1;
                var grid = (DataGrid)window.FindName("LyricsGrid");
                Assert.True(grid.IsReadOnly);
                Assert.Equal("歌词获取 / 预览", ((TabItem)window.FindName("LyricSourceTab")).Header);
                var current = (RecordingLyricLine)window.FindName("CurrentRecordingLine");
                var next = (RecordingLyricLine)window.FindName("NextRecordingLine");
                Tap(1);
                Call("BrowseRecordingLines", 2);
                Assert.Equal(document.Lines[2].Id, current.LineId); // Includes non-singing lines.
                Assert.Equal(document.Lines[3].Id, next.LineId);
                Assert.Equal(0, grid.SelectedIndex);
                Assert.Equal(ProjectTime.Zero, playback.Position);
                playback.Position = At(5); Call("UiTimer_Tick", null, EventArgs.Empty);
                Assert.Equal(document.Lines[2].Id, current.LineId); // Timer cannot steal the browsing viewport.
                Call("RecordFromBrowsedLine_Click", window, new RoutedEventArgs());
                Assert.Equal(0, grid.SelectedIndex); Assert.Single(Markers());
                Assert.False((bool)Call("CanEditRecordingStructure")!);
                var distant = document.Lines[3];
                Call("RecordingUnitSelected", distant.Id, distant.Units[1].Id);
                ((TextBox)window.FindName("QuickEditText")).Text = "色";
                Call("BrowseRecordingLines", 1);
                Assert.Equal(document.Lines[2].Id, current.LineId); // Dirty quick-edit draft is not discarded.
                ((Task)Call("ApplyQuickEditAsync")!).GetAwaiter().GetResult();
                var session = (WorkspaceSession)Field("_workspace").GetValue(window)!;
                Assert.Equal("秋色", session.Project.Lyrics.Lines[3].PrimaryText);
                Assert.Equal(document.Lines[0].Units.Select(unit => unit.Id), session.Project.Lyrics.Lines[0].Units.Select(unit => unit.Id));
                Assert.Equal(new[] { At(1) }, Markers());
                Call("BrowseRecordingLines", 999); Assert.Equal(document.Lines[^1].Id, current.LineId); Assert.Null(next.LineId);
                Call("BrowseRecordingLines", -999); Assert.Equal(document.Lines[0].Id, current.LineId);
                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                Call("RecordingLyrics_MouseWheel", window, wheel);
                Assert.True(wheel.Handled); Assert.Equal(document.Lines[1].Id, current.LineId);
                Tap(2); Assert.Equal(document.Lines[1].Id, current.LineId); // Working line advances after 春/风, not the browsed line.
                Assert.Null(Field("_browsedRecordingLineId").GetValue(window));
                Call("UndoLastMarker"); Assert.Equal(document.Lines[0].Id, current.LineId);
                Call("BrowseRecordingLines", 4); Call("FollowRecording_Click", window, new RoutedEventArgs());
                Assert.Equal(document.Lines[0].Id, current.LineId); Assert.Single(Markers());
                Call("ResetMarkers"); Call("RefreshMarkerList"); Call("BrowseRecordingLines", 4);
                Call("RecordFromBrowsedLine_Click", window, new RoutedEventArgs());
                Assert.Equal(4, grid.SelectedIndex); Assert.Equal(At(5), playback.Position);
                Tap(6); Assert.Equal(document.Lines[4].Id, current.LineId);

                // The acquisition language selector cannot silently change the mounted document on save.
                ((ComboBox)window.FindName("LyricLanguageCombo")).SelectedIndex = 0;
                Call("ApplyLyricRowsToProject");
                session = (WorkspaceSession)Field("_workspace").GetValue(window)!;
                Assert.Equal(LyricLanguage.Chinese, session.Project.Lyrics.Language);
                new JsonProjectRepository().SaveAsync(root, session.Project).GetAwaiter().GetResult();
                Assert.Equal("秋色", new JsonProjectRepository().LoadAsync(root).GetAwaiter().GetResult().Lyrics.Lines[3].PrimaryText);

                // A structural edit keeps browsing attached to line identity, and removing all lines is safe.
                Call("ResetMarkers"); Call("RefreshMarkerList");
                var reordered = session.Project.Lyrics.Lines.Reverse().ToArray();
                Call("CommitRecordingLines", reordered, distant.Id);
                Assert.Equal(distant.Id, current.LineId);
                Call("CommitRecordingLines", Array.Empty<LyricLine>(), null);
                Call("BrowseRecordingLines", 1); Assert.Null(current.LineId); Assert.Null(next.LineId);
                completion.SetResult();

                object? Call(string name, params object?[] args) => Method(name).Invoke(window, args);
                List<ProjectTime> Markers() => (List<ProjectTime>)Field("_markers").GetValue(window)!;
                void Tap(double seconds) { Assert.True((bool)Call("RecordMarker", At(seconds))!); Call("RefreshMarkerList"); }
            }
            catch (Exception exception) { completion.SetException(exception); }
            finally
            {
                if (window is not null) { Field("_allowClose").SetValue(window, true); window.Close(); }
                if (Directory.Exists(root)) { Directory.Delete(root, true); }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    private static ProjectTime At(double seconds) => ProjectTime.FromSeconds(seconds);
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private sealed class Playback : IAudioPlaybackSession
    {
        public ProjectTime Position { get; set; }
        public bool IsPlaying { get; private set; }
        public bool IsLoaded => true;
        public ProjectDuration Duration => ProjectDuration.FromSeconds(100);
        public string? SourcePath => null;
        public Task LoadAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PlayAsync(CancellationToken cancellationToken = default) { IsPlaying = true; return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken = default) { IsPlaying = false; return Task.CompletedTask; }
        public Task SeekAsync(ProjectTime position, CancellationToken cancellationToken = default) { Position = position; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
