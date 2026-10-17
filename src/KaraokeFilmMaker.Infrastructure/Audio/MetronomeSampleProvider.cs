using KaraokeFilmMaker.Domain.Time;
using NAudio.Wave;

namespace KaraokeFilmMaker.Infrastructure.Audio;

/// <summary>
/// Adds clicks at exact source sample positions. Sharing the music's WASAPI buffer means
/// no UI timer, wall clock, or separate audio device can drift from the master transport.
/// </summary>
public sealed class MetronomeSampleProvider : ISampleProvider
{
    private const double ClickDurationSeconds = 0.025;
    private readonly ISampleProvider _source;
    private readonly MetronomeSettings _settings;
    private readonly double _physicalStartSeconds;
    private readonly double _projectRate;
    private readonly double _periodSeconds;
    private readonly double _offsetSeconds;
    private long _samplesRead;

    public MetronomeSampleProvider(ISampleProvider source, MetronomeSettings settings,
        double physicalStartSeconds = 0, double projectRate = 1)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (!double.IsFinite(physicalStartSeconds) || physicalStartSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalStartSeconds));
        }
        if (!double.IsFinite(projectRate) || projectRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(projectRate));
        }

        _source = source;
        _settings = settings;
        _physicalStartSeconds = physicalStartSeconds;
        _projectRate = projectRate;
        _periodSeconds = 60 / settings.BeatsPerMinute;
        _offsetSeconds = settings.OffsetMilliseconds / 1_000d;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        int read = _source.Read(buffer);
        if (_settings.Enabled)
        {
            int channels = WaveFormat.Channels;
            int sampleRate = WaveFormat.SampleRate;
            // Derive every beat from the absolute sample index, not accumulated periods.
            // It stays deterministic across arbitrary Read buffer sizes and long playback.
            for (int index = 0; index < read; index++)
            {
                long frame = (_samplesRead + index) / channels;
                double projectSeconds = (_physicalStartSeconds + frame / (double)sampleRate) * _projectRate;
                double relative = projectSeconds - _offsetSeconds;
                double beatIndex = Math.Floor(relative / _periodSeconds + 1e-10);
                double beatSeconds = _offsetSeconds + beatIndex * _periodSeconds;
                double elapsed = (projectSeconds - beatSeconds) / _projectRate;
                if (beatIndex >= 0 && beatSeconds >= -1e-9 && elapsed >= -1e-9 && elapsed < ClickDurationSeconds)
                {
                    elapsed = Math.Max(0, elapsed);
                    double envelope = Math.Exp(-elapsed * 180) * (1 - elapsed / ClickDurationSeconds);
                    double click = 0.45 * Math.Cos(2 * Math.PI * 1_500 * elapsed) * envelope;
                    buffer[index] = (float)Math.Clamp(buffer[index] + click, -1, 1);
                }
            }
        }
        _samplesRead += read;
        return read;
    }
}
