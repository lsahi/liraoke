using System.IO;
using System.Windows;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Audio;

namespace KaraokeFilmMaker.App.Practice;

public partial class PracticeRoomWindow
{
    internal Func<Task<bool>>? MakeBacking { get; set; }
    internal Func<string?>? BackingPath { get; set; }
    internal Action? CoverSaved { get; set; }
    private readonly string _draftDir = Path.Combine(Path.GetTempPath(), "Liraoke-Take-" + Guid.NewGuid().ToString("N"));
    private string? _voice;
    private double _takeStart, _takeEnd;
    private bool _recording, _reviewing, _restoreMonitor;
    private PracticeAudioSession Recorder => (PracticeAudioSession)_audio;
    private const int DefaultOffset = -150;
    private int _offsetMs = DefaultOffset;
    private void Earlier_Click(object sender, RoutedEventArgs e) => _offsetMs -= 30;
    private void Later_Click(object sender, RoutedEventArgs e) => _offsetMs += 30;
    private void ResetOffset_Click(object sender, RoutedEventArgs e) => _offsetMs = DefaultOffset;

    private async void Calibrate_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_audio.IsPlaying) await _audio.PauseAsync();
        Status.Text = "请用正常演唱音量唱四秒，包含一句中最响的字。";
        try
        {
            double peak = await VocalCalibration.MeasureAsync(DeviceId(InputDevice), value => MicLevel.Value = value, _lifetime.Token);
            if (peak < 0.01) { Status.Text = "麦克风信号太小，请检查输入设备或提高输入增益后重试。"; return; }
            if (peak >= 0.99) { Status.Text = "输入接近削波，请降低麦克风或声卡的输入增益后重试。"; return; }
            VocalGain.Value = Math.Clamp(-6 - 20 * Math.Log10(peak), VocalGain.Minimum, VocalGain.Maximum);
            Status.Text = "已按试唱电平调整翻唱人声音量，可试听后继续微调。";
        }
        finally { MicLevel.Value = 0; }
    });

    private void UpdateTakeControls()
    {
        RecordButton.IsEnabled = _ready && _audio is PracticeAudioSession;
        RecordButton.Content = _recording ? "结束录歌" : "开始录歌";
        MixTab.IsEnabled = !_recording;
        ConnectButton.IsEnabled = BackingButton.IsEnabled = InputDevice.IsEnabled = OutputDevice.IsEnabled =
            VersionCombo.IsEnabled = MonitorCheck.IsEnabled = RoutePanel.IsEnabled = !_recording;
        PlayButton.IsEnabled = PositionSlider.IsEnabled = _ready && !_recording;
        ReviewButton.IsEnabled = SaveTakeButton.IsEnabled = !_recording && _takeEnd > _takeStart && File.Exists(BackingPath?.Invoke());
    }

    private async void Backing_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_audio.IsPlaying) await _audio.PauseAsync();
        if (MakeBacking != null && await MakeBacking()) AccompanimentItem.IsEnabled = true;
    });

    private async void Record_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_recording) { await FinishTakeAsync(); return; }
        if (!_ready) return;
        if (_reviewing) await ConnectAsync();
        if (_audio.IsPlaying) await _audio.PauseAsync();
        var area = _project.GetWorkArea();
        if (_audio.Position >= area.End) await _audio.SeekAsync(area.Start);
        Directory.CreateDirectory(_draftDir);
        _restoreMonitor = _audio.IsMonitoring;
        string next = Path.Combine(_draftDir, Guid.NewGuid() + ".wav");
        try
        {
            if (!_restoreMonitor) await _audio.SetMonitoringAsync(DeviceId(InputDevice), true);
            ApplyVolume();
            await _audio.PlayAsync();
            double start = _audio.Position.TotalSeconds;
            Recorder.StartTake(next);
            if (_voice != null) File.Delete(_voice);
            _voice = next;
            _takeStart = _takeEnd = start;
            _recording = true;
            Status.Text = $"正在录歌 · 原曲 {start:F2} 秒开始";
        }
        catch
        {
            Recorder.EndTake();
            if (_audio.IsPlaying) await _audio.PauseAsync();
            if (!_restoreMonitor) await _audio.SetMonitoringAsync(null, false);
            throw;
        }
    });

    private async Task FinishTakeAsync()
    {
        double end = _audio.Position.TotalSeconds;
        _recording = false;
        Recorder.EndTake();
        await _audio.PauseAsync();
        if (!_restoreMonitor) await _audio.SetMonitoringAsync(null, false);
        _takeEnd = end;
        Status.Text = $"已录制 {_takeStart:F2} 至 {end:F2} 秒。制作伴奏后可试听、调整人声偏移并保存。";
    }

    private async Task RenderTakeAsync(string path)
    {
        if (BackingPath?.Invoke() is not string backing || !File.Exists(backing))
            throw new InvalidOperationException("请先制作伴奏。");
        var master = _project.MediaAssets.First(asset => asset.Id == _project.MasterAudioAssetId);
        await PracticeCover.MixAsync(backing, _voice!, path, _takeStart, _takeEnd, _offsetMs, master.Duration.TotalSeconds, _lifetime.Token, VocalGain.Value);
    }

    private async void Review_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        string path = Path.Combine(_draftDir, "review.wav");
        await _audio.DisconnectAsync();
        _ready = false;
        MonitorCheck.IsChecked = false;
        await RenderTakeAsync(path);
        await _audio.OpenAsync(path, _project.GetWorkArea(), DeviceId(OutputDevice));
        _reviewing = _ready = true;
        await _audio.SeekAsync(ProjectTime.FromSeconds(Math.Max(_project.GetWorkArea().Start.TotalSeconds, _takeStart - 1)));
        await _audio.PlayAsync();
        Status.Text = "正在试听翻唱。修改偏移后可再次试听。";
    });

    private async void SaveTake_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        string target = PracticeCover.PathFor(_root, _project.MasterAudioAssetId!.Value);
        if (File.Exists(target) && MessageBox.Show(this, "用本次演唱覆盖已保存的翻唱？", "保存翻唱",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp = Path.Combine(Path.GetDirectoryName(target)!, Guid.NewGuid() + ".partial.flac");
        try
        {
            await RenderTakeAsync(temp);
            File.Move(temp, target, true);
            CoverSaved?.Invoke();
            Status.Text = "翻唱已保存，可在预览页选择「翻唱」。";
        }
        finally { File.Delete(temp); }
    });

    private void CleanDraft()
    {
        try { if (Directory.Exists(_draftDir)) Directory.Delete(_draftDir, true); }
        catch (IOException) { }
    }
}
