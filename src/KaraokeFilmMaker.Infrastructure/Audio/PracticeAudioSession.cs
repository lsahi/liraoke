using KaraokeFilmMaker.Application.Practice;
using KaraokeFilmMaker.Domain.Time;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KaraokeFilmMaker.Infrastructure.Audio;

public sealed class PracticeAudioSession : IPracticeAudioSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WasapiPlayer? _player;
    private WasapiRecorder? _capture;
    private MMDevice? _outputDevice, _inputDevice;
    private WaveFileReader? _song;
    private PracticeMixProvider? _mix;
    private PracticeMonitorBuffer? _monitor;
    private ISampleProvider? _microphone;
    private TimeRange _range;
    private ProjectTime _anchor;
    private bool _playing, _disposed;
    private float _songVolume = 0.7f, _micVolume = 0.7f;
    private bool _muted = true;
    private string? _deviceError;
    public static IReadOnlyList<PracticeAudioDevice> GetDevices(bool input)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<PracticeAudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(input ? DataFlow.Capture : DataFlow.Render, DeviceState.Active))
        {
            using (device) devices.Add(new(device.ID, device.FriendlyName));
        }
        return devices;
    }

    public ProjectTime Position
    {
        get
        {
            if (!_playing || _player is null) return _anchor;
            double seconds = (double)_player.GetPosition() / _player.OutputWaveFormat.AverageBytesPerSecond;
            return ProjectTime.FromSeconds(Math.Min(_range.End.TotalSeconds, _anchor.TotalSeconds + seconds));
        }
    }
    public bool IsPlaying => _playing && DeviceError is null && _player?.PlaybackState == PlaybackState.Playing && Position < _range.End;
    public bool IsMonitoring => _capture is not null;
    public string? DeviceError => Volatile.Read(ref _deviceError);
    public float SongVolume { get => _songVolume; set { _songVolume = Gain(value); if (_mix is not null) _mix.SongVolume = _songVolume; } }
    public float MicrophoneVolume { get => _micVolume; set { _micVolume = Gain(value); if (_mix is not null) _mix.MicrophoneVolume = _micVolume; } }
    public bool MicrophoneMuted { get => _muted; set { _muted = value; if (_mix is not null) _mix.MicrophoneMuted = value; } }
    private static float Gain(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    public async Task OpenAsync(string wavePath, TimeRange range, string? outputDeviceId)
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await ReleaseAsync();
            _deviceError = null;
            _song = new WaveFileReader(wavePath);
            if (_song.WaveFormat.SampleRate != 48000 || _song.WaveFormat.Channels != 2)
                throw new InvalidOperationException("练歌房需要 48 kHz 双声道播放缓存。");
            if (range.Duration.Microseconds <= 0 || range.End.TotalSeconds > _song.TotalTime.TotalSeconds + 0.05)
                throw new InvalidOperationException("项目播放范围超出歌曲缓存。");
            _range = range;
            _anchor = range.Start;
            using var enumerator = new MMDeviceEnumerator();
            _outputDevice = outputDeviceId is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(outputDeviceId);
            await RebuildAsync(range.Start, false);
        }
        catch { await ReleaseAsync(); throw; }
        finally { _gate.Release(); }
    }

    public async Task SetMonitoringAsync(string? inputDeviceId, bool enabled)
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_song is null) throw new InvalidOperationException("请先连接播放设备。");
            var position = Position; bool resume = IsPlaying;
            await StopOutputAsync();
            await StopCaptureAsync();
            if (enabled)
            {
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    _inputDevice = inputDeviceId is null
                        ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                        : enumerator.GetDevice(inputDeviceId);
                    StartCapture();
                }
                catch
                {
                    await StopCaptureAsync();
                    await RebuildAsync(position, resume);
                    throw;
                }
            }
            await RebuildAsync(position, resume);
        }
        finally { _gate.Release(); }
    }

    private void StartCapture()
    {
        var builder = new WasapiRecorderBuilder().WithDevice(_inputDevice!).WithSharedMode()
            .WithEventSync().WithBufferLength(20).WithLowLatency();
        _capture = builder.Build();
        var buffer = new PracticeMonitorBuffer(_capture.WaveFormat);
        _monitor = buffer;
        ISampleProvider source = buffer.ToSampleProvider();
        if (source.WaveFormat.Channels == 1) source = new MonoToStereoSampleProvider(source);
        else if (source.WaveFormat.Channels != 2)
            throw new NotSupportedException("请选择单声道或双声道麦克风。");
        _microphone = source.WaveFormat.SampleRate == 48000 ? source : new WdlResamplingSampleProvider(source, 48000);
        _capture.DataAvailable += (data, flags, devicePosition, qpcPosition) =>
        {
            if ((flags & AudioClientBufferFlags.Silent) != 0) buffer.Add(new byte[data.Length]);
            else buffer.Add(data);
        };
        _capture.RecordingStopped += CaptureStopped;
        _capture.StartRecording();
    }

    public Task PlayAsync() => ChangeTransportAsync(null, true);
    public Task PauseAsync() => ChangeTransportAsync(null, false);
    public Task SeekAsync(ProjectTime position) => ChangeTransportAsync(position, null);
    private async Task ChangeTransportAsync(ProjectTime? position, bool? playing)
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_song is null) throw new InvalidOperationException("请先连接播放设备。");
            if (DeviceError is not null) throw new InvalidOperationException(DeviceError);
            bool resume = playing ?? IsPlaying;
            var target = position ?? Position;
            if (playing == true && target >= _range.End) target = _range.Start;
            target = new ProjectTime(Math.Clamp(target.Microseconds, _range.Start.Microseconds, _range.End.Microseconds));
            await RebuildAsync(target, resume);
        }
        finally { _gate.Release(); }
    }

    private async Task RebuildAsync(ProjectTime position, bool play)
    {
        await StopOutputAsync();
        _monitor?.Clear();
        _song!.CurrentTime = TimeSpan.FromSeconds(position.TotalSeconds);
        _anchor = position;
        _playing = play;
        _mix = new PracticeMixProvider(_song.ToSampleProvider(), _microphone ?? new SilenceProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).ToSampleProvider(),
            play ? (long)Math.Round((_range.End.TotalSeconds - position.TotalSeconds) * 48000) * 2 : 0)
        { SongVolume = _songVolume, MicrophoneVolume = _micVolume, MicrophoneMuted = _muted };
        try
        {
            StartOutput();
        }
        catch (Exception error)
        {
            _playing = false;
            Volatile.Write(ref _deviceError, "请重新连接音频设备：" + error.Message);
            await StopOutputAsync();
            throw;
        }
    }
    private void StartOutput()
    {
        var builder = new WasapiPlayerBuilder().WithDevice(_outputDevice!).WithSharedMode().WithEventSync()
            .WithLatency(20).WithLowLatency().WithMmcssThreadPriority("Pro Audio");
        _player = builder.Build();
        _player.PlaybackStopped += OutputStopped;
        _player.Init(_mix!.ToWaveProvider());
        _player.Play(); // Always drain the microphone, even while the song is paused.
    }
    private void OutputStopped(object? sender, StoppedEventArgs e)
    { if (e.Exception is not null) Volatile.Write(ref _deviceError, "输出设备已中断，请重新连接设备：" + e.Exception.Message); }
    private void CaptureStopped(object? sender, StoppedEventArgs e)
    { if (e.Exception is not null) Volatile.Write(ref _deviceError, "麦克风已中断，请重新连接设备：" + e.Exception.Message); }

    private async Task StopOutputAsync()
    {
        if (_player is not null)
        {
            _player.PlaybackStopped -= OutputStopped;
            await _player.DisposeAsync(); _player = null;
        }
    }
    private async Task StopCaptureAsync()
    {
        await DisposeCaptureStreamAsync();
        _inputDevice?.Dispose(); _inputDevice = null;
    }
    private async Task DisposeCaptureStreamAsync()
    {
        if (_capture is not null)
        {
            _capture.RecordingStopped -= CaptureStopped;
            await _capture.DisposeAsync(); _capture = null;
        }
        _monitor?.Clear(); _monitor = null; _microphone = null;
    }
    private async Task ReleaseAsync()
    {
        try { await StopOutputAsync(); }
        finally
        {
            try { await StopCaptureAsync(); }
            finally { _song?.Dispose(); _song = null; _outputDevice?.Dispose(); _outputDevice = null; _playing = false; }
        }
    }
    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try { if (!_disposed) await ReleaseAsync(); }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { if (_disposed) return; _disposed = true; await ReleaseAsync(); }
        finally { _gate.Release(); }
    }
}
