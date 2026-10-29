using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using KaraokeFilmMaker.App.Practice;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Tests;

public class PracticeRoomWasapiPlaybackTests
{
    [Fact]
    public async Task MainEntryPausesEditorAndRoomUsesActualImageVideoClockAndClosesBeforeEditing()
    {
        string root = Path.Combine(Path.GetTempPath(), "Liraoke.PracticeMedia", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tools = MediaToolchain.Find();
        try
        {
            await MediaProcess.RunAsync(tools.FfmpegPath, ["-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "3", Path.Combine(root, "song.wav")]);
            await MediaProcess.RunAsync(tools.FfmpegPath, ["-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=24", "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p", Path.Combine(root, "background.mp4")]);
            await MediaProcess.RunAsync(tools.FfmpegPath, ["-v", "error", "-y", "-i", Path.Combine(root, "background.mp4"), "-frames:v", "1", "-update", "1", Path.Combine(root, "still.png")]);
            var song = new MediaAsset(Guid.NewGuid(), "song.wav", "song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(3));
            var image = new MediaAsset(Guid.NewGuid(), "still.png", "still.png", MediaKind.Image, ProjectDuration.Zero);
            var video = new MediaAsset(Guid.NewGuid(), "background.mp4", "background.mp4", MediaKind.Video, ProjectDuration.FromSeconds(1));
            var project = KaraokeProject.Create("媒体集成测试", DateTimeOffset.UnixEpoch) with
            {
                MediaAssets = [song, image, video], MasterAudioAssetId = song.Id,
                VideoClips = [new VideoClip(Guid.NewGuid(), image.Id, At(0), At(0), ProjectDuration.FromSeconds(1), true),
                    new VideoClip(Guid.NewGuid(), video.Id, At(1), At(0), ProjectDuration.FromSeconds(1), true)],
                WorkArea = new(At(.1), At(2.5))
            };
            await PracticeRoomTests.Sta(async () =>
            {
                var main = new MainWindow(new UserPreferenceStore(root));
                try
                {
                    Field("_workspace").SetValue(main, new WorkspaceSession(root, project));
                    Call(main, "RefreshWorkspaceDisplay"); main.Show();
                    await (Task)Call(main, "PrepareProjectPlaybackAsync", true)!;
                    var editorAudio = (IAudioPlaybackSession)Field("_playback").GetValue(main)!;
                    await editorAudio.PlayAsync(); await Task.Delay(100);
                    ((Button)main.FindName("PracticeRoomButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    try { await Until(() => Field("_practiceRoom").GetValue(main) is PracticeRoomWindow); }
                    catch (Exception error)
                    {
                        throw new InvalidOperationException($"Practice open failed: {((TextBlock)main.FindName("StatusText")).Text}; busy={Field("_isBusy").GetValue(main)}; opening={Field("_openingPracticeRoom").GetValue(main)}", error);
                    }
                    var room = (PracticeRoomWindow)Field("_practiceRoom").GetValue(main)!;
                    await Until(() => ((Button)room.FindName("PlayButton")).IsEnabled);
                    Assert.False(editorAudio.IsPlaying);
                    Assert.True(main.IsEnabled);
                    Assert.Equal(Visibility.Visible, ((Image)room.FindName("Still")).Visibility);
                    var position = (Slider)room.FindName("PositionSlider");
                    position.Value = 1.4;
                    room.GetType().GetMethod("Seek_KeyUp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(room,
                        [position, new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                            PresentationSource.FromVisual(room)!, 0, System.Windows.Input.Key.Right)]);
                    await Until(() => ((MediaElement)room.FindName("Video")).Visibility == Visibility.Visible);
                    var media = (MediaElement)room.FindName("Video"); Assert.True(media.IsMuted);
                    Assert.InRange(media.Position.TotalSeconds, .25, .6);
                    position.Value = 2.2;
                    room.GetType().GetMethod("Seek_KeyUp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(room,
                        [position, new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                            PresentationSource.FromVisual(room)!, 0, System.Windows.Input.Key.Right)]);
                    await Until(() => media.Visibility == Visibility.Hidden);
                    ((Button)room.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => ((TextBlock)room.FindName("TimeText")).Text.StartsWith("00:02"));
                    await Task.Delay(500);
                    Assert.Equal("播放", ((Button)room.FindName("PlayButton")).Content);
                    string original = ProjectRecoveryStore.Content(((WorkspaceSession)Field("_workspace").GetValue(main)!).Project);
                    await (Task)Call(main, "RunUiOperationAsync", (Func<Task>)(() => Task.CompletedTask), false)!;
                    Assert.False(room.IsVisible); Assert.Null(Field("_practiceRoom").GetValue(main));
                    Assert.Equal(original, ProjectRecoveryStore.Content(((WorkspaceSession)Field("_workspace").GetValue(main)!).Project));
                }
                finally
                {
                    await (Task)Call(main, "ClosePracticeRoomAsync")!;
                    Field("_allowClose").SetValue(main, true); main.Close();
                }
            });
        }
        finally { Directory.Delete(root, true); }
    }
    private static ProjectTime At(double seconds) => ProjectTime.FromSeconds(seconds);
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static object? Call(MainWindow main, string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, args);
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition()) { Assert.True(DateTime.UtcNow < deadline, "Media or UI did not become ready."); await Task.Delay(25); }
    }
}
