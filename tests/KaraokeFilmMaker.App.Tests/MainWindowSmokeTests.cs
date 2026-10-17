using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Tests;

public sealed class MainWindowSmokeTests
{
    [Fact]
    public async Task WindowLoadsAllMvpTabsAndKeepsLyricEditsWhenSaved()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "KaraokeFilmMaker.UiTests", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            try
            {
                Directory.CreateDirectory(root);
                window = new MainWindow();
                Assert.Equal("莉拉OK! / Liraoke", window.Title);
                Assert.NotNull(window.Icon);
                Assert.Equal("Liraoke", typeof(MainWindow).Assembly.GetName().Name);
                var icon = BitmapDecoder.Create(new Uri("pack://application:,,,/Liraoke;component/Assets/Liraoke.ico"),
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, icon.Frames.Select(frame => frame.PixelWidth));
                Assert.All(icon.Frames, frame => Assert.Equal(frame.PixelWidth, frame.PixelHeight));
                var video = new MediaAsset(Guid.NewGuid(), "日文歌曲背景.mp4", "media/video/background.mp4", MediaKind.Video, ProjectDuration.FromSeconds(10));
                KaraokeProject project = KaraokeProject.Create("冒烟检查", DateTimeOffset.UtcNow) with
                {
                    Lyrics = new LyricImportService().Parse("[00:01]未来へ進む\n[00:03]次の歌", ".lrc", LyricLanguage.Japanese),
                    MediaAssets = [video],
                    VideoClips = [new VideoClip(Guid.NewGuid(), video.Id, ProjectTime.Zero, ProjectTime.Zero, ProjectDuration.FromSeconds(5), true),
                        new VideoClip(Guid.NewGuid(), video.Id, ProjectTime.FromSeconds(5), ProjectTime.FromSeconds(1), ProjectDuration.FromSeconds(3), false)],
                };
                Field("_workspace").SetValue(window, new WorkspaceSession(root, project));
                foreach (string method in new[] { "RefreshWorkspaceDisplay", "RefreshLyricRows", "RefreshVideoRows", "RefreshSubtitleStyleFields", "RefreshExportPreset" })
                {
                    Method(method).Invoke(window, null);
                }
                var tabs = (TabControl)window.FindName("EditorTabs");
                Assert.Equal("冒烟检查 — 莉拉OK! / Liraoke", window.Title);
                Assert.Equal(5, tabs.Items.Count);
                foreach (var tab in tabs.Items)
                {
                    tabs.SelectedItem = tab;
                    window.Measure(new Size(1200, 900)); window.Arrange(new Rect(0, 0, 1200, 900)); window.UpdateLayout();
                }
                var monitor = (FrameworkElement)window.FindName("SharedMonitor");
                ((DataGrid)window.FindName("VideoGrid")).SelectedIndex = 0;
                ((ProjectTimelineControl)window.FindName("ProjectTimeline")).Waveform = new KaraokeFilmMaker.Infrastructure.Audio.WaveformData(5, 100,
                    Enumerable.Range(0, 500).Select(index => (float)(Math.Abs(Math.Sin(index * 0.2)) * 0.65 + 0.1)).ToArray());
                tabs.SelectedIndex = 1;
                Assert.Same(monitor, ((ContentControl)window.FindName("RecordingMonitorHost")).Content);
                Assert.Null(((ContentControl)window.FindName("PreviewMonitorHost")).Content);
                Assert.True(((ProjectTimelineControl)window.FindName("ProjectTimeline")).ShowLyricUnits);
                tabs.SelectedIndex = 0;
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                Assert.Null(window.FindName("InspectorTrackText"));
                Assert.Same(monitor, ((ContentControl)window.FindName("PreviewMonitorHost")).Content);
                Assert.False(((ProjectTimelineControl)window.FindName("ProjectTimeline")).ShowLyricUnits);
                var lyrics = (DataGrid)window.FindName("LyricsGrid");
                Assert.Equal(2, lyrics.Items.Count);
                object row = lyrics.Items[0];
                row.GetType().GetProperty("PrimaryText")!.SetValue(row, "修正后的歌词");
                Method("ApplyLyricRowsToProject").Invoke(window, null);
                var session = (WorkspaceSession)Field("_workspace").GetValue(window)!;
                Assert.Equal("修正后的歌词", session.Project.Lyrics.Lines[0].PrimaryText);
                Guid unitId = session.Project.Lyrics.Lines[0].Units[0].Id;
                var source = new LyricSource("User supplied", "demo", "作者", null, "lyrics/owned.txt", DateTimeOffset.UtcNow);
                session = session with { Project = session.Project with { Lyrics = session.Project.Lyrics with { Source = source } } };
                Field("_workspace").SetValue(window, session);
                Method("ApplyLyricRowsToProject").Invoke(window, null);
                Assert.Equal(unitId, ((WorkspaceSession)Field("_workspace").GetValue(window)!).Project.Lyrics.Lines[0].Units[0].Id);
                Assert.Equal(source, ((WorkspaceSession)Field("_workspace").GetValue(window)!).Project.Lyrics.Source);
                var repository = new JsonProjectRepository();
                repository.SaveAsync(root, session.Project).GetAwaiter().GetResult();
                var reloaded = repository.LoadAsync(root).GetAwaiter().GetResult();
                Assert.Equal(session.Project.Lyrics.Lines[0].Timing, reloaded.Lyrics.Lines[0].Timing);
                Assert.Equal("修正后的歌词", reloaded.Lyrics.Lines[0].PrimaryText);
                string? snapshots = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
                if (!string.IsNullOrWhiteSpace(snapshots))
                {
                    Directory.CreateDirectory(snapshots);
                    var content = (FrameworkElement)window.Content;
                    for (int index = 0; index < 3; index++)
                    {
                        tabs.SelectedIndex = index;
                        Method("RefreshLyricPreview").Invoke(window, [ProjectTime.FromSeconds(2)]);
                        var bitmap = new RenderTargetBitmap(1440, 940, 96, 96, PixelFormats.Pbgra32);
                        content.Measure(new Size(1440, 940)); content.Arrange(new Rect(0, 0, 1440, 940)); content.UpdateLayout();
                        Assert.True(((FrameworkElement)window.FindName("ProjectTimeline")).ActualHeight >= 100);
                        bitmap.Render(content);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(snapshots, index switch
                        { 0 => "editor-preview.png", 1 => "editor-recording.png", _ => "lyric-source-preview.png" })); encoder.Save(file);
                    }
                }
                finished.TrySetResult();
            }
            catch (Exception exception) { finished.TrySetException(exception); }
            finally
            {
                if (window is not null) { Field("_allowClose").SetValue(window, true); }
                window?.Close();
                if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
}
