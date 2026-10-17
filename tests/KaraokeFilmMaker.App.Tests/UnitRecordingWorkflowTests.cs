using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.Application.Japanese;
using KaraokeFilmMaker.Infrastructure.Japanese;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KaraokeFilmMaker.App.Tests;

public sealed class UnitRecordingWorkflowTests
{
    [Fact]
    public async Task DefaultWindowWorkflowRecordsCharactersCrossesLinesAndCanUndoWithoutLineTiming()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            string root = Path.Combine(Path.GetTempPath(), "KaraokeFilmMaker.UnitRecordingTests", Guid.NewGuid().ToString("N"));
            try
            {
                window = new MainWindow();
                ((IAudioPlaybackSession)Field("_playback").GetValue(window)!).DisposeAsync().AsTask().GetAwaiter().GetResult();
                var playback = new StubPlayback(); Field("_playback").SetValue(window, playback);
                var project = KaraokeProject.Create("逐字回归", DateTimeOffset.UtcNow) with
                { Lyrics = new LyricImportService().Parse("春风\n明天", ".txt", LyricLanguage.Chinese) };
                Field("_workspace").SetValue(window, new WorkspaceSession(root, project));
                Method("RefreshLyricRows").Invoke(window, null); Method("RefreshVideoRows").Invoke(window, null);
                ((TabControl)window.FindName("EditorTabs")).SelectedIndex = 1;
                Assert.Equal(1, ((ComboBox)window.FindName("TapModeCombo")).SelectedIndex);
                Assert.Contains("春", Hint(window));
                Tap(window, 1); Assert.Contains("风", Hint(window));
                Assert.Single(Markers(window)); // One space/tap is one character, not a whole line.
                Tap(window, 2); Assert.Contains("明", Hint(window));
                Assert.Equal(1, ((DataGrid)window.FindName("LyricsGrid")).SelectedIndex);
                Method("UndoLastMarker").Invoke(window, null);
                Assert.Contains("风", Hint(window)); Assert.Equal(0, ((DataGrid)window.FindName("LyricsGrid")).SelectedIndex);
                Method("RedoMarkerButton_Click").Invoke(window, [window, new RoutedEventArgs()]);
                Assert.Contains("明", Hint(window));
                Tap(window, 4); Assert.Contains("天", Hint(window));
                Tap(window, 5); Assert.Contains("尾字结束", Hint(window));
                Tap(window, 7);
                Assert.False((bool)Method("RecordMarker").Invoke(window, [At(8)])!);
                playback.Position = At(8);
                Method("ApplyUnitMarkersButton_Click").Invoke(window, [window, new RoutedEventArgs()]);
                var session = (WorkspaceSession)Field("_workspace").GetValue(window)!;
                Assert.Equal(new[] { 1d, 2, 4, 5 }, session.Project.Lyrics.Lines.SelectMany(line => line.Units).Select(unit => unit.Timing.Start.TotalSeconds));
                Assert.Equal(7, session.Project.Lyrics.Lines[^1].Units[^1].Timing.End.TotalSeconds);
                Assert.Empty(Markers(window));
                var repository = new JsonProjectRepository();
                repository.SaveAsync(root, session.Project).GetAwaiter().GetResult();
                var loaded = repository.LoadAsync(root).GetAwaiter().GetResult();
                Assert.Equal(session.Project.Lyrics.Lines[^1].Units[^1].Timing, loaded.Lyrics.Lines[^1].Units[^1].Timing);

                // Complete only the first sentence with Enter, then continue directly with the next.
                Field("_workspace").SetValue(window, new WorkspaceSession(root, project));
                Method("RefreshLyricRows").Invoke(window, null);
                ((DataGrid)window.FindName("LyricsGrid")).SelectedIndex = 0;
                Tap(window, 1); Tap(window, 2); playback.Position = At(3);
                Method("ApplyUnitMarkersButton_Click").Invoke(window, [window, new RoutedEventArgs()]);
                session = (WorkspaceSession)Field("_workspace").GetValue(window)!;
                Assert.Equal(3, session.Project.Lyrics.Lines[0].Timing.End.TotalSeconds);
                Assert.Equal(project.Lyrics.Lines[1], session.Project.Lyrics.Lines[1]);
                Assert.Equal(1, ((DataGrid)window.FindName("LyricsGrid")).SelectedIndex);
                Tap(window, 5); Tap(window, 6); playback.Position = At(8);
                Method("ApplyUnitMarkersButton_Click").Invoke(window, [window, new RoutedEventArgs()]);
                session = (WorkspaceSession)Field("_workspace").GetValue(window)!;
                Assert.Equal(3, session.Project.Lyrics.Lines[0].Timing.End.TotalSeconds);
                Assert.Equal(8, session.Project.Lyrics.Lines[1].Timing.End.TotalSeconds);

                // Japanese reading is a real recording prerequisite, and editing it rebuilds the tap plan.
                project = project with { Lyrics = new LyricImportService().Parse("戦場ヶ原", ".txt", LyricLanguage.Japanese) };
                Field("_workspace").SetValue(window, new WorkspaceSession(root, project));
                Method("RefreshLyricRows").Invoke(window, null);
                Assert.False((bool)Method("RecordMarker").Invoke(window, [At(1)])!);
                Assert.Empty(Markers(window));
                Assert.Contains("先生成", ((TextBlock)window.FindName("StatusText")).Text);
                object row = ((DataGrid)window.FindName("LyricsGrid")).Items[0];
                row.GetType().GetProperty("ReadingText")!.SetValue(row, "せんじょうがはら");
                Method("ApplyLyricRowsToProject").Invoke(window, null);
                Method("RefreshLyricRows").Invoke(window, null);
                Tap(window, 1);
                Assert.Contains("じょ", Hint(window)); Assert.DoesNotContain("jo", Hint(window));
                session = (WorkspaceSession)Field("_workspace").GetValue(window)!;
                Assert.Equal("せん", session.Project.Lyrics.Lines[0].Units[0].Reading);
                Assert.Equal("sen", session.Project.Lyrics.Lines[0].Units[0].Romanization);
                Assert.Equal(6, session.Project.Lyrics.Lines[0].Units.Count);

                Method("ResetMarkers").Invoke(window, null);
                project = project with { Lyrics = new LyricImportService().Parse("Hello, don't stop", ".txt", LyricLanguage.English) };
                Field("_workspace").SetValue(window, new WorkspaceSession(root, project));
                Method("RefreshLyricRows").Invoke(window, null);
                Assert.Equal(2, ((ComboBox)window.FindName("LyricLanguageCombo")).SelectedIndex);
                Tap(window, 1);
                Assert.Contains("don't", Hint(window));
                Assert.Single(Markers(window));

                // Two visible rows advance by taps; quick line edits preserve the pending tap sequence.
                Method("ResetMarkers").Invoke(window, null);
                project = project with { Lyrics = new LyricImportService().Parse("きょうは\nいっしょに\nうたおう", ".txt", LyricLanguage.Japanese) };
                Field("_workspace").SetValue(window, new WorkspaceSession(root, project));
                Method("RefreshLyricRows").Invoke(window, null);
                Method("RefreshSubtitleStyleFields").Invoke(window, null);
                Method("RefreshTapHint").Invoke(window, [At(0)]);
                var currentLine = (RecordingLyricLine)window.FindName("CurrentRecordingLine");
                var nextLine = (RecordingLyricLine)window.FindName("NextRecordingLine");
                Assert.Equal(project.Lyrics.Lines[0].Id, currentLine.LineId);
                Assert.Equal(project.Lyrics.Lines[1].Id, nextLine.LineId);
                Assert.False(((CheckBox)window.FindName("ShowRomajiCheckBox")).IsChecked);
                Tap(window, 1);
                Method("RecordingUnitSelected").Invoke(window, [project.Lyrics.Lines[0].Id, project.Lyrics.Lines[0].Units[1].Id]);
                ((Task)Method("EditRecordingBoundaryAsync").Invoke(window, [false])!).GetAwaiter().GetResult();
                Assert.Single(Markers(window));
                session = (WorkspaceSession)Field("_workspace").GetValue(window)!;
                Assert.Equal(4, session.Project.Lyrics.Lines.Count);
                Assert.Equal("きょ", session.Project.Lyrics.Lines[0].PrimaryText);
                Assert.Equal("うは", session.Project.Lyrics.Lines[1].PrimaryText);
                Assert.Equal(session.Project.Lyrics.Lines[1].Id, currentLine.LineId);
                Assert.Equal(session.Project.Lyrics.Lines[2].Id, nextLine.LineId);
                Method("RecordingUnitSelected").Invoke(window, [session.Project.Lyrics.Lines[0].Id, session.Project.Lyrics.Lines[0].Units[0].Id]);
                ((Task)Method("EditRecordingBoundaryAsync").Invoke(window, [true])!).GetAwaiter().GetResult();
                Assert.Single(Markers(window));
                ((Task)Method("RestoreHistoryAsync").Invoke(window, [false])!).GetAwaiter().GetResult();
                Assert.Equal(4, ((WorkspaceSession)Field("_workspace").GetValue(window)!).Project.Lyrics.Lines.Count);
                Assert.Single(Markers(window));
                ((Task)Method("RestoreHistoryAsync").Invoke(window, [true])!).GetAwaiter().GetResult();
                Assert.Equal(3, ((WorkspaceSession)Field("_workspace").GetValue(window)!).Project.Lyrics.Lines.Count);
                Assert.Single(Markers(window));
                Tap(window, 2); Tap(window, 3);
                Assert.Equal(project.Lyrics.Lines[1].Id, currentLine.LineId);
                Assert.Equal(project.Lyrics.Lines[2].Id, nextLine.LineId);
                Method("UndoLastMarker").Invoke(window, null);
                Assert.Equal(project.Lyrics.Lines[0].Id, currentLine.LineId);

                var ids = ((WorkspaceSession)Field("_workspace").GetValue(window)!).Project.Lyrics.Lines.SelectMany(line => line.Units).Select(unit => unit.Id).ToArray();
                ((CheckBox)window.FindName("ShowRomajiCheckBox")).IsChecked = true;
                Assert.True(((CheckBox)window.FindName("StyleShowRomajiCheckBox")).IsChecked);
                session = (WorkspaceSession)Field("_workspace").GetValue(window)!;
                Assert.True(session.Project.SubtitleStyle.ShowRomanization);
                Assert.Equal(ids, session.Project.Lyrics.Lines.SelectMany(line => line.Units).Select(unit => unit.Id));
                repository.SaveAsync(root, session.Project).GetAwaiter().GetResult();
                Assert.True(repository.LoadAsync(root).GetAwaiter().GetResult().SubtitleStyle.ShowRomanization);

                // Recording does not update or draw the subtitle overlay; preview still does.
                var overlay = (SubtitleOverlay)window.FindName("SubtitlePreview");
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                var overlayTime = typeof(SubtitleOverlay).GetField("_time", BindingFlags.NonPublic | BindingFlags.Instance)!;
                object oldTime = overlayTime.GetValue(overlay)!;
                Method("RefreshLyricPreview").Invoke(window, [At(5)]);
                Assert.Equal(oldTime, overlayTime.GetValue(overlay));
                ((TabControl)window.FindName("EditorTabs")).SelectedIndex = 0;
                Method("RefreshLyricPreview").Invoke(window, [At(5)]);
                Assert.Equal(Visibility.Visible, overlay.Visibility); Assert.Equal(At(5), overlayTime.GetValue(overlay));
                ((TabControl)window.FindName("EditorTabs")).SelectedIndex = 1;

                // Inline text/reading correction preserves pending points, selection and the undo stack.
                Method("ResetMarkers").Invoke(window, null);
                project = project with { Lyrics = new LyricImportService().Parse("春风", ".txt", LyricLanguage.Chinese) };
                Field("_workspace").SetValue(window, new WorkspaceSession(root, project));
                Method("RefreshLyricRows").Invoke(window, null);
                Tap(window, 1);
                var list = (ListBox)window.FindName("MarkerList"); list.SelectedIndex = 0;
                object firstItem = list.Items[0]; Tap(window, 2);
                Assert.Same(firstItem, list.Items[0]); Assert.Equal(0, list.SelectedIndex);
                Method("UndoLastMarker").Invoke(window, null);
                var target = project.Lyrics.Lines[0];
                Method("RecordingUnitSelected").Invoke(window, [target.Id, target.Units[0].Id]);
                Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("QuickEditPanel")).Visibility);
                ((TextBox)window.FindName("QuickEditText")).Text = "清";
                ((TextBox)window.FindName("QuickEditReading")).Text = "qing";
                ((Task)Method("ApplyQuickEditAsync").Invoke(window, null)!).GetAwaiter().GetResult();
                session = (WorkspaceSession)Field("_workspace").GetValue(window)!;
                Assert.Equal("清风", session.Project.Lyrics.Lines[0].PrimaryText);
                Assert.Equal("qing", session.Project.Lyrics.Lines[0].Units[0].Reading);
                Assert.Equal(target.Units.Select(unit => unit.Id), session.Project.Lyrics.Lines[0].Units.Select(unit => unit.Id));
                Assert.Equal(new[] { At(1) }, Markers(window));
                Method("RedoMarkerButton_Click").Invoke(window, [window, new RoutedEventArgs()]);
                Assert.Equal(new[] { At(1), At(2) }, Markers(window));
                Assert.Contains("清", list.Items[0].ToString());
                ((Task)Method("RestoreHistoryAsync").Invoke(window, [false])!).GetAwaiter().GetResult();
                Assert.Equal("春", ((TextBox)window.FindName("QuickEditText")).Text);
                Assert.Equal(new[] { At(1), At(2) }, Markers(window));
                ((Task)Method("RestoreHistoryAsync").Invoke(window, [true])!).GetAwaiter().GetResult();
                Assert.Equal("清", ((TextBox)window.FindName("QuickEditText")).Text);
                Assert.Equal(new[] { At(1), At(2) }, Markers(window));
                Method("ExportButton_Click").Invoke(window, [window, new RoutedEventArgs()]);
                Assert.Contains("未应用", ((TextBlock)window.FindName("StatusText")).Text);

                // A long sentence stays on one horizontal row and follows the active kana.
                var longLine = new LyricImportService().Parse(new string('あ', 60), ".txt", LyricLanguage.Japanese).Lines[0];
                var strip = new RecordingLyricLine();
                strip.Update(longLine, LyricLanguage.Japanese, false, 45, 45, null);
                var scroll = new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
                scroll.Measure(new Size(300, 100)); scroll.Arrange(new Rect(0, 0, 300, 100)); scroll.UpdateLayout();
                strip.RevealActive(scroll); scroll.UpdateLayout();
                Assert.True(scroll.HorizontalOffset > 500);
                Assert.True(strip.ActualHeight < 100);

                string? snapshots = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
                if (!string.IsNullOrWhiteSpace(snapshots))
                {
                    using var dictionary = new MeCabJapaneseReadingService();
                    var document = new LyricImportService().Parse("[00:01]一緒に行きましょうといったら、すぐにディスコタイムをスタット。\n[00:08]次の歌を歌おう\n[00:12]みんなで", ".lrc", LyricLanguage.Japanese);
                    document = new JapaneseAnnotationService(dictionary).AnnotateAsync(document).GetAwaiter().GetResult();
                    Method("ResetMarkers").Invoke(window, null);
                    Field("_workspace").SetValue(window, new WorkspaceSession(root, project with { Lyrics = document }));
                    Method("RefreshLyricRows").Invoke(window, null);
                    Method("RefreshSubtitleStyleFields").Invoke(window, null);
                    Method("RefreshWorkspaceDisplay").Invoke(window, null);
                    Method("RefreshMarkerList").Invoke(window, null);
                    Method("RecordingUnitSelected").Invoke(window, [document.Lines[0].Id, document.Lines[0].Units[0].Id]);
                    ((TextBlock)window.FindName("StatusText")).Text = "假名为打轴依据；可点击演唱单元调整换行。";
                    Directory.CreateDirectory(snapshots);
                    foreach (bool showRomaji in new[] { false, true })
                    {
                        ((CheckBox)window.FindName("ShowRomajiCheckBox")).IsChecked = showRomaji;
                        Method("RefreshTapHint").Invoke(window, [At(2)]);
                        Method("RefreshLyricPreview").Invoke(window, [At(2)]);
                        var content = (FrameworkElement)window.Content;
                        content.Measure(new Size(1440, 940)); content.Arrange(new Rect(0, 0, 1440, 940)); content.UpdateLayout();
                        var bitmap = new RenderTargetBitmap(1440, 940, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(content);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create(Path.Combine(snapshots, showRomaji ? "recording-romaji-reference.png" : "recording-kana-two-lines.png"));
                        encoder.Save(output);
                    }
                }
                finished.TrySetResult();
            }
            catch (Exception exception) { finished.TrySetException(exception); }
            finally
            {
                if (window is not null) { Field("_allowClose").SetValue(window, true); window.Close(); }
                if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static ProjectTime At(double seconds) => ProjectTime.FromSeconds(seconds);
    private static string Hint(MainWindow window) => ((TextBlock)window.FindName("NextTapText")).Text;
    private static List<ProjectTime> Markers(MainWindow window) => (List<ProjectTime>)Field("_markers").GetValue(window)!;
    private static void Tap(MainWindow window, double seconds)
    {
        Assert.True((bool)Method("RecordMarker").Invoke(window, [At(seconds)])!);
        Method("RefreshMarkerList").Invoke(window, null);
    }
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private sealed class StubPlayback : IAudioPlaybackSession
    {
        public ProjectTime Position { get; set; }
        public bool IsPlaying { get; private set; }
        public bool IsLoaded => true;
        public ProjectDuration Duration => ProjectDuration.FromSeconds(10);
        public string? SourcePath => null;
        public Task LoadAsync(string filePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PlayAsync(CancellationToken cancellationToken = default) { IsPlaying = true; return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken = default) { IsPlaying = false; return Task.CompletedTask; }
        public Task SeekAsync(ProjectTime position, CancellationToken cancellationToken = default) { Position = position; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
