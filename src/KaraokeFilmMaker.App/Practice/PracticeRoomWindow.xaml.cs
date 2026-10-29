using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Practice;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Application.Timeline;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;
using KaraokeFilmMaker.Infrastructure.Audio;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Workspaces;

namespace KaraokeFilmMaker.App.Practice;

/// <summary>Owns its transport and immutable project snapshot; has no dependency on MainWindow or AI.</summary>
public partial class PracticeRoomWindow : Window
{
    private readonly KaraokeProject _project;
    private readonly string _root;
    private readonly IReadOnlyDictionary<Guid, string> _previews;
    private readonly AudioVisualizationData? _visualization;
    private readonly IPracticeAudioSession _audio;
    private readonly Func<bool, CancellationToken, Task<string>> _prepare;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private Task _operation = Task.CompletedTask;
    private Task? _closing;
    private bool _busy, _ready, _seeking, _allowClose, _videoReady;
    private bool _videoPlaying;
    private TimeSpan? _lastVideoSeek;
    private Guid? _clipId;
    private readonly HashSet<Guid> _failedVideo = [];

    public PracticeRoomWindow(KaraokeProject project, string root, IReadOnlyDictionary<Guid, string> previews,
        AudioVisualizationData? visualization, bool accompaniment,
        Func<bool, CancellationToken, Task<string>> prepare,
        IPracticeAudioSession? audio = null,
        IReadOnlyList<PracticeAudioDevice>? inputs = null, IReadOnlyList<PracticeAudioDevice>? outputs = null)
    {
        _project = project; _root = root; _previews = new Dictionary<Guid, string>(previews);
        _visualization = visualization; _prepare = prepare;
        _audio = audio ?? new PracticeAudioSession();
        InitializeComponent();
        SongTitle.Text = project.Title;
        Stage.Width = 960; Stage.Height = 960d * project.ExportPreset.Height / project.ExportPreset.Width;
        AccompanimentItem.IsEnabled = project.GetAccompaniment(project.MasterAudioAssetId) is not null;
        VersionCombo.SelectedIndex = accompaniment && AccompanimentItem.IsEnabled ? 1 : 0;
        try
        {
            InputDevice.ItemsSource = new[] { new PracticeAudioDevice("", "系统默认麦克风") }.Concat(inputs ?? PracticeAudioSession.GetDevices(true)).ToArray();
            OutputDevice.ItemsSource = new[] { new PracticeAudioDevice("", "系统默认输出") }.Concat(outputs ?? PracticeAudioSession.GetDevices(false)).ToArray();
            InputDevice.SelectedIndex = OutputDevice.SelectedIndex = 0;
        }
        catch (Exception error) { Status.Text = "无法枚举设备，可尝试连接系统默认设备：" + error.Message; }
        PositionSlider.Minimum = project.GetWorkArea().Start.TotalSeconds;
        PositionSlider.Maximum = Math.Max(PositionSlider.Minimum + 0.001, project.GetWorkArea().End.TotalSeconds);
        _timer.Tick += Timer_Tick;
        Draw(project.GetWorkArea().Start);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) { _timer.Start(); await RunAsync(ConnectAsync); }
    private async void Connect_Click(object sender, RoutedEventArgs e) => await RunAsync(ConnectAsync);
    private static string? DeviceId(ComboBox combo) => combo.SelectedItem is PracticeAudioDevice { Id.Length: > 0 } device ? device.Id : null;
    private async Task ConnectAsync()
    {
        ProjectTime position = _ready ? _audio.Position : _project.GetWorkArea().Start;
        _ready = false;
        await _audio.DisconnectAsync();
        MonitorCheck.IsChecked = false;
        Status.Text = "正在准备项目音频…";
        string wave = await _prepare(VersionCombo.SelectedIndex == 1, _lifetime.Token);
        _lifetime.Token.ThrowIfCancellationRequested();
        await _audio.OpenAsync(wave, _project.GetWorkArea(), DeviceId(OutputDevice));
        await _audio.SeekAsync(position);
        _ready = true;
        ApplyVolume();
        Status.Text = "已连接。播放歌曲后可开启麦克风监听；关闭窗口释放设备。";
        Draw(position);
    }
    private async void Play_Click(object sender, RoutedEventArgs e) => await RunAsync(ToggleAsync);
    private async Task ToggleAsync()
    {
        if (!_ready) return;
        if (_audio.IsPlaying) await _audio.PauseAsync(); else await _audio.PlayAsync();
        Draw(_audio.Position);
    }
    private async void Monitor_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!_ready) { MonitorCheck.IsChecked = false; return; }
        try { await _audio.SetMonitoringAsync(DeviceId(InputDevice), MonitorCheck.IsChecked == true); }
        finally { MonitorCheck.IsChecked = _audio.IsMonitoring; ApplyVolume(); }
        Status.Text = _audio.IsMonitoring ? "麦克风监听已开启，不保存录音。" : "麦克风监听已关闭，设备已释放。";
    });
    private void Volume_Changed(object sender, RoutedEventArgs e) { if (IsInitialized) ApplyVolume(); }
    private void ApplyVolume()
    {
        _audio.SongVolume = (float)SongVolume.Value;
        _audio.MicrophoneVolume = (float)MicVolume.Value;
        _audio.MicrophoneMuted = MuteCheck.IsChecked == true || MonitorCheck.IsChecked != true;
    }
    private void Seek_Begin(object sender, MouseButtonEventArgs e) => _seeking = true;
    private async void Seek_End(object sender, MouseButtonEventArgs e)
    {
        double target = PositionSlider.Value;
        _seeking = false;
        await RunAsync(() => _audio.SeekAsync(ProjectTime.FromSeconds(target)));
    }
    private async void Seek_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageDown or Key.PageUp)
            await RunAsync(() => _audio.SeekAsync(ProjectTime.FromSeconds(PositionSlider.Value)));
    }
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.OriginalSource is not (TextBox or ComboBox or Slider or Button or CheckBox))
        { e.Handled = true; await RunAsync(ToggleAsync); }
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _lifetime.IsCancellationRequested) return;
        _operation = PerformAsync(action);
        await _operation;
    }
    private async Task PerformAsync(Func<Task> action)
    {
        _busy = true; SettingsPanel.IsEnabled = PlayButton.IsEnabled = PositionSlider.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Status.Text = "练歌房：" + error.Message; }
        finally
        {
            _busy = false;
            if (!_lifetime.IsCancellationRequested)
            {
                SettingsPanel.IsEnabled = true; PlayButton.IsEnabled = PositionSlider.IsEnabled = _ready;
                PlayButton.Content = _audio.IsPlaying ? "暂停" : "播放";
            }
        }
    }
    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_busy || !_ready || _lifetime.IsCancellationRequested) return;
        if (_audio.DeviceError is { } deviceError)
        {
            _ready = false; MonitorCheck.IsChecked = false; Video.Pause();
            PlayButton.IsEnabled = PositionSlider.IsEnabled = false;
            Status.Text = deviceError;
            await RunAsync(() => _audio.DisconnectAsync());
            return;
        }
        try { Draw(_audio.Position); }
        catch (Exception error)
        {
            _ready = false; MonitorCheck.IsChecked = false; Video.Pause();
            Status.Text = "播放已中断，请重新连接：" + error.Message;
            await RunAsync(() => _audio.DisconnectAsync());
        }
    }
    private void Draw(ProjectTime position)
    {
        Subtitles.Update(_project, position);
        Visualization.Update(_project.AudioVisualizationEnabled ? _visualization : null, position, _project.AudioVisualizationStyle);
        if (!_seeking && !PositionSlider.IsKeyboardFocusWithin) PositionSlider.Value = position.TotalSeconds;
        TimeText.Text = $"{TimeSpan.FromSeconds(position.TotalSeconds):mm\\:ss} / {TimeSpan.FromSeconds(_project.GetWorkArea().End.TotalSeconds):mm\\:ss}";
        PlayButton.Content = _ready && _audio.IsPlaying ? "暂停" : "播放";
        VideoClip? clip = VideoTimelineService.VisibleClipAt(_project, position);
        if (clip is null || _failedVideo.Contains(clip.MediaAssetId)) { ClearVideo(); return; }
        if (_clipId != clip.Id)
        {
            ClearVideo(); _clipId = clip.Id;
            MediaAsset asset = _project.MediaAssets.First(item => item.Id == clip.MediaAssetId);
            string path = _previews.TryGetValue(asset.Id, out string? proxy) ? proxy : WorkspacePaths.AssetPath(_root, asset);
            if (asset.Kind == MediaKind.Image)
            {
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path); image.EndInit(); image.Freeze();
                Still.Source = image; Still.Visibility = Visibility.Visible;
            }
            else { Video.Source = new Uri(path); Video.Play(); Video.Pause(); }
        }
        if (!_videoReady) return;
        var expected = TimeSpan.FromSeconds(clip.SourceStart.TotalSeconds + position.TotalSeconds - clip.TimelineStart.TotalSeconds);
        bool playing = _audio.IsPlaying;
        if ((!playing && _lastVideoSeek != expected) || (playing && Math.Abs((Video.Position - expected).TotalMilliseconds) > 100))
        { Video.Position = expected; _lastVideoSeek = expected; }
        if (_videoPlaying != playing)
        { if (playing) Video.Play(); else Video.Pause(); _videoPlaying = playing; }
    }
    private void Video_Opened(object sender, RoutedEventArgs e) { _videoReady = true; Video.Visibility = Visibility.Visible; if (_ready) Draw(_audio.Position); }
    private void Video_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        var clip = _project.VideoClips.FirstOrDefault(item => item.Id == _clipId);
        if (clip is not null) _failedVideo.Add(clip.MediaAssetId);
        ClearVideo(); Status.Text = "背景视频暂不可用，音频和字幕仍可播放：" + e.ErrorException.Message;
    }
    private void ClearVideo()
    {
        if (_clipId is null) return;
        Video.Close(); Video.Source = null; Video.Visibility = Visibility.Hidden;
        Still.Source = null; Still.Visibility = Visibility.Collapsed; _clipId = null; _videoReady = false;
        _videoPlaying = false; _lastVideoSeek = null;
    }
    public Task CloseRoomAsync() => _closing ??= ShutdownAsync();
    private async Task ShutdownAsync()
    {
        _lifetime.Cancel(); _timer.Stop(); Video.Close();
        // Leave WPF's Closing callback before invoking Close again, even if disposal completes synchronously.
        await Dispatcher.Yield(DispatcherPriority.Background);
        await _operation;
        try { await _audio.DisposeAsync(); }
        finally { _allowClose = true; if (IsVisible) Close(); }
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    { if (!_allowClose) { e.Cancel = true; await CloseRoomAsync(); } }
    private async void Window_Closed(object? sender, EventArgs e) { if (!_allowClose) await CloseRoomAsync(); }
}
