using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Practice;

public sealed record PracticeAudioDevice(string Id, string Name);

/// <summary>A private, non-recording transport. Project time comes from rendered song audio.</summary>
public interface IPracticeAudioSession : IAsyncDisposable
{
    ProjectTime Position { get; }
    bool IsPlaying { get; }
    bool IsMonitoring { get; }
    string? DeviceError { get; }
    float SongVolume { get; set; }
    float MicrophoneVolume { get; set; }
    bool MicrophoneMuted { get; set; }
    Task OpenAsync(string wavePath, TimeRange range, string? outputDeviceId);
    Task DisconnectAsync();
    Task SetMonitoringAsync(string? inputDeviceId, bool enabled);
    Task PlayAsync();
    Task PauseAsync();
    Task SeekAsync(ProjectTime position);
}
