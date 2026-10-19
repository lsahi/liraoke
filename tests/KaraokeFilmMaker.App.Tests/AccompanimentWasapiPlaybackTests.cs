using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Separation;

namespace KaraokeFilmMaker.App.Tests;

// Name intentionally matches the existing no-audio-endpoint CI exclusion.
public class AccompanimentWasapiPlaybackTests
{
    [Fact]
    public async Task ToggleAndModeSwitchPreserveSongClockPauseSeekAndSlowPlayback()
    {
        string root = Path.Combine(Path.GetTempPath(), "Liraoke.AccompanimentPlayback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tools = MediaToolchain.Find();
        await MediaProcess.RunAsync(tools.FfmpegPath, ["-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "4", Path.Combine(root, "song.wav")]);
        var asset = new MediaAsset(Guid.NewGuid(), "silence", "song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(4));
        var project = KaraokeProject.Create("preview", DateTimeOffset.UtcNow) with { MediaAssets = [asset], MasterAudioAssetId = asset.Id };
        project = await new AccompanimentStore(tools).RegisterAsync(root, project, asset.Id, Path.Combine(root, "song.wav"));
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                MainWindow? window = null;
                try
                {
                    window = new MainWindow();
                    Field("_workspace").SetValue(window, new WorkspaceSession(root, project));
                    var tabs = (TabControl)window.FindName("EditorTabs"); tabs.SelectedItem = window.FindName("PreviewTab");
                    Call(window, "RefreshWorkspaceDisplay");
                    await (Task)Call(window, "PrepareProjectPlaybackAsync", true)!;
                    var playback = (IAudioPlaybackSession)Field("_playback").GetValue(window)!;
                    string originalPath = playback.SourcePath!;
                    await playback.SeekAsync(ProjectTime.FromSeconds(1));
                    var toggle = (CheckBox)window.FindName("AccompanimentPreviewCheck");
                    toggle.IsChecked = true; Call(window, "AccompanimentPreview_Click", toggle, new RoutedEventArgs());
                    await Idle(window);
                    string accompanimentPath = playback.SourcePath!;
                    Assert.NotEqual(originalPath, accompanimentPath); Assert.True(toggle.IsChecked);
                    Assert.InRange(playback.Position.TotalSeconds, 0.99, 1.01); Assert.False(playback.IsPlaying);
                    await playback.PlayAsync(); await Task.Delay(120);
                    tabs.SelectedItem = window.FindName("RecordingTab"); await Idle(window);
                    Assert.Equal(originalPath, playback.SourcePath); Assert.True(playback.IsPlaying);
                    Assert.InRange(playback.Position.TotalSeconds, 1, 2);
                    tabs.SelectedItem = window.FindName("PreviewTab"); await Idle(window);
                    Assert.Equal(accompanimentPath, playback.SourcePath); Assert.True(playback.IsPlaying);
                    await playback.PauseAsync();
                    Field("_playbackRate").SetValue(window, 0.5);
                    await (Task)Call(window, "PrepareProjectPlaybackAsync", false)!;
                    await playback.SeekAsync(ProjectTime.FromSeconds(3.5));
                    Assert.InRange(playback.Position.TotalSeconds, 3.49, 3.51);
                    await playback.PlayAsync(); await Task.Delay(150); await playback.PauseAsync();
                    Assert.InRange(playback.Position.TotalSeconds, 3.52, 3.8);
                    await playback.SeekAsync(ProjectTime.FromSeconds(4)); Assert.Equal(4, playback.Position.TotalSeconds, 3);
                    completed.TrySetResult();
                }
                catch (Exception e) { completed.TrySetException(e); }
                finally
                {
                    if (window is not null) { Field("_allowClose").SetValue(window, true); window.Close(); }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
        finally { thread.Join(TimeSpan.FromSeconds(5)); Directory.Delete(root, true); }
    }
    private static async Task Idle(MainWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((bool)Field("_isBusy").GetValue(window)!)
        { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(10); }
    }
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static object? Call(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
}
