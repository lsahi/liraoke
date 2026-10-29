using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.App.Practice;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Application.Practice;
using KaraokeFilmMaker.Application.Preferences;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Tests;

public class PracticeRoomTests
{
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(960, 720)]
    [InlineData(720, 720)]
    [InlineData(720, 1280)]
    public Task PlaybackCanvasPreservesOutputAspectRatio(int width, int height) => Sta(async () =>
    {
        var project = Project(); project = project with { ExportPreset = project.ExportPreset with { Width = width, Height = height } };
        var room = Room(project, Path.GetTempPath(), new FakeAudio(), (_, _) => Task.FromResult("song.wav"));
        try
        {
            room.Width = 860; room.Height = 620; room.Show();
            await Until(() => Field<Button>(room, "PlayButton").IsEnabled); room.UpdateLayout();
            var stage = Field<Grid>(room, "Stage");
            Assert.Equal((double)width / height, stage.ActualWidth / stage.ActualHeight, 5);
            var controls = Field<StackPanel>(room, "SettingsPanel");
            Assert.True(controls.ActualWidth > 700); Assert.True(controls.ActualHeight > 100);
            Snapshot(room, $"practice-aspect-{width}x{height}.png");
        }
        finally { await room.CloseRoomAsync(); }
    });

    [Fact]
    public Task IndependentRoomPreservesAiDraftLayoutProjectAndConfiguration() => Sta(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "Liraoke.PracticeUi", Guid.NewGuid().ToString("N"));
        MainWindow? main = null; PracticeRoomWindow? room = null;
        try
        {
            var store = new UserPreferenceStore(root);
            store.SaveAiProvider(Guid.NewGuid(), "AI 隔离回归", false, "https://example.invalid/v1", new AiCredentials(APIKey: "test-only-key"));
            byte[] preferencesBefore = File.ReadAllBytes(Path.Combine(root, "user-preferences.json"));
            main = new MainWindow(store); main.Show();
            var chat = (AiChatPanel)main.FindName("AssistantPanel");
            ((Button)main.FindName("AiOpenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var draft = (TextBox)chat.FindName("MessageInput"); draft.Text = "AI 草稿保留";
            var editor = (FrameworkElement)main.FindName("PreviewMonitorHost"); main.UpdateLayout();
            var size = (editor.ActualWidth, editor.ActualHeight);
            var project = Project(); string before = ProjectRecoveryStore.Content(project);
            var audio = new FakeAudio();
            room = Room(project, root, audio, (_, _) => Task.FromResult("prepared.wav")); room.Owner = main;
            room.Show(); await Until(() => Field<Button>(room, "PlayButton").IsEnabled);
            Assert.True(main.IsEnabled); Assert.Equal(size, (editor.ActualWidth, editor.ActualHeight));
            Field<Button>(room, "PlayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => audio.IsPlaying);
            await audio.SeekAsync(ProjectTime.FromSeconds(3)); await Task.Delay(40);
            var monitor = Field<CheckBox>(room, "MonitorCheck"); monitor.IsChecked = true;
            monitor.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); await Until(() => audio.IsMonitoring);
            Field<Slider>(room, "SongVolume").Value = .4; Field<Slider>(room, "MicVolume").Value = .3;
            Assert.Equal(.4f, audio.SongVolume); Assert.Equal(.3f, audio.MicrophoneVolume); Assert.False(audio.MicrophoneMuted);
            Field<CheckBox>(room, "MuteCheck").IsChecked = true;
            Field<CheckBox>(room, "MuteCheck").RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); Assert.True(audio.MicrophoneMuted);
            foreach (var dimensions in new[] { (1120, 800), (860, 620) })
            {
                room.Width = dimensions.Item1; room.Height = dimensions.Item2; room.UpdateLayout();
                Assert.True(Field<Grid>(room, "Stage").ActualHeight > 0);
                Snapshot(room, $"practice-{dimensions.Item1}.png");
            }
            // The modeless room does not block the main assistant or consume its input.
            typeof(AiChatPanel).GetMethod("Send", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(chat, null);
            Assert.Equal("AI暂未接入", ((TextBlock)((StackPanel)chat.FindName("Messages")).Children[^1]).Text);
            draft.Text = "关闭练歌房后仍保留";
            await room.CloseRoomAsync(); Assert.True(audio.Disposed); Assert.False(audio.IsMonitoring);
            Assert.Equal("关闭练歌房后仍保留", draft.Text); Assert.Equal(Visibility.Visible, chat.Visibility);
            Assert.Equal(before, ProjectRecoveryStore.Content(project));
            Assert.Equal(preferencesBefore, File.ReadAllBytes(Path.Combine(root, "user-preferences.json")));
            main.Close(); main = null; room = null;
        }
        finally
        {
            if (room is not null) await room.CloseRoomAsync(); main?.Close();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [Fact]
    public Task ClosingDuringPreparationCancelsAndDoesNotOpenDevices() => Sta(async () =>
    {
        var audio = new FakeAudio(); bool cancelled = false;
        var room = Room(Project(), Path.GetTempPath(), audio, async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); return "never.wav"; }
            catch (OperationCanceledException) { cancelled = true; throw; }
        });
        room.Show(); await Task.Delay(50);
        room.Close(); await Until(() => !room.IsVisible);
        Assert.True(cancelled); Assert.True(audio.Disposed); Assert.Equal(0, audio.OpenCount);
    });

    [Fact]
    public Task FailedMicrophoneAndDeviceLossKeepWindowRecoverable() => Sta(async () =>
    {
        var audio = new FakeAudio { FailMicrophone = true };
        var room = Room(Project(), Path.GetTempPath(), audio, (_, _) => Task.FromResult("song.wav"));
        try
        {
            room.Show(); await Until(() => Field<Button>(room, "PlayButton").IsEnabled);
            var monitor = Field<CheckBox>(room, "MonitorCheck"); monitor.IsChecked = true;
            monitor.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            await Until(() => Field<TextBlock>(room, "Status").Text.Contains("测试拒绝"));
            Assert.False(monitor.IsChecked); Assert.True(Field<Button>(room, "PlayButton").IsEnabled);
            audio.Error = "测试设备断开";
            await Until(() => !Field<Button>(room, "PlayButton").IsEnabled);
            Assert.True(Field<Button>(room, "ConnectButton").IsEnabled);
            Field<Button>(room, "ConnectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => audio.OpenCount == 2 && Field<Button>(room, "PlayButton").IsEnabled);
        }
        finally { await room.CloseRoomAsync(); }
    });

    private static PracticeRoomWindow Room(KaraokeProject project, string root, FakeAudio audio,
        Func<bool, CancellationToken, Task<string>> prepare) => new(project, root, new Dictionary<Guid, string>(), null, false,
            prepare, audio, [], []);
    private static KaraokeProject Project()
    {
        var asset = new MediaAsset(Guid.NewGuid(), "练习曲.wav", "song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(60));
        var lyrics = new LyricImportService().Parse("一起唱出这首歌\n让声音陪伴你", ".txt", LyricLanguage.Chinese);
        lyrics = new LyricTimingService().ApplyContinuousUnitMarkers(lyrics,
            Enumerable.Range(0, 7).Select(i => ProjectTime.FromSeconds(1 + i)).ToArray(), ProjectTime.FromSeconds(60), 0, ProjectTime.FromSeconds(9), explicitLineEnd: true);
        return KaraokeProject.Create("练习曲 · Sing with Liraoke", DateTimeOffset.UnixEpoch) with
        { MediaAssets = [asset], MasterAudioAssetId = asset.Id, Lyrics = lyrics };
    }
    private static T Field<T>(PracticeRoomWindow window, string name) => (T)window.FindName(name);
    private static async Task Until(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { Assert.True(DateTime.UtcNow < end, "UI operation timed out"); await Task.Delay(15); }
    }
    internal static async Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
    private static void Snapshot(FrameworkElement content, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR"); if (directory is null) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
    }
    private sealed class FakeAudio : IPracticeAudioSession
    {
        public ProjectTime Position { get; private set; } = ProjectTime.FromSeconds(3);
        public bool IsPlaying { get; private set; }
        public bool IsMonitoring { get; private set; }
        public string? Error;
        public string? DeviceError => Error;
        public bool FailMicrophone, Disposed;
        public int OpenCount;
        public float SongVolume { get; set; }
        public float MicrophoneVolume { get; set; }
        public bool MicrophoneMuted { get; set; }
        public Task OpenAsync(string path, TimeRange range, string? output) { OpenCount++; Error = null; return Task.CompletedTask; }
        public Task DisconnectAsync() { IsPlaying = IsMonitoring = false; return Task.CompletedTask; }
        public Task SetMonitoringAsync(string? input, bool enabled)
        { if (enabled && FailMicrophone) throw new InvalidOperationException("测试拒绝麦克风访问"); IsMonitoring = enabled; return Task.CompletedTask; }
        public Task PlayAsync() { IsPlaying = true; return Task.CompletedTask; }
        public Task PauseAsync() { IsPlaying = false; return Task.CompletedTask; }
        public Task SeekAsync(ProjectTime time) { Position = time; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsPlaying = IsMonitoring = false; Disposed = true; return ValueTask.CompletedTask; }
    }
}
