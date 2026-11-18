using NAudio.Wave;

namespace KaraokeFilmMaker.Infrastructure.Audio;

/// <summary>One stereo stream for the song and live microphone; never writes captured audio.</summary>
public sealed class PracticeMixProvider(ISampleProvider song, ISampleProvider microphone, long songSamples) : ISampleProvider
{
    private long _remaining = songSamples;
    private float[] _scratch = [];
    public WaveFormat WaveFormat => song.WaveFormat;
    public float SongVolume { get; set; } = 0.7f;
    public float MicrophoneVolume { get; set; } = 0.7f;
    public bool MicrophoneMuted { get; set; } = true;

    public int Read(Span<float> buffer)
    {
        buffer.Clear();
        int requested = (int)Math.Min(buffer.Length, _remaining);
        int read = requested > 0 ? song.Read(buffer[..requested]) : 0;
        _remaining -= read;
        if (read < requested) _remaining = 0;
        if (_scratch.Length < buffer.Length) _scratch = new float[buffer.Length];
        var mic = _scratch.AsSpan(0, buffer.Length);
        mic.Clear();
        microphone.Read(mic); // Drain even while muted; unmute must never replay old voice.
        float songGain = SongVolume, micGain = MicrophoneMuted ? 0 : MicrophoneVolume;
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = Math.Clamp(buffer[i] * songGain + mic[i] * micGain, -1f, 1f);
        return buffer.Length; // Monitor continues while song is paused or has ended.
    }
}

/// <summary>Bounded live queue. A stalled renderer discards stale voice rather than accumulating delay.</summary>
public sealed class PracticeMonitorBuffer : IWaveProvider
{
    private readonly object _gate = new();
    private readonly BufferedWaveProvider _buffer;
    public PracticeMonitorBuffer(WaveFormat format)
    {
        _buffer = new BufferedWaveProvider(format, TimeSpan.FromMilliseconds(120))
        { DiscardOnBufferOverflow = true, ReadFully = true };
    }
    public WaveFormat WaveFormat => _buffer.WaveFormat;
    public int BufferedBytes { get { lock (_gate) return _buffer.BufferedBytes; } }
    public void Add(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            int limit = WaveFormat.AverageBytesPerSecond * 60 / 1000;
            if (_buffer.BufferedBytes + bytes.Length > limit) _buffer.ClearBuffer();
            // Keep the newest part of an unusually large packet, aligned to complete frames.
            int length = Math.Min(bytes.Length, limit / WaveFormat.BlockAlign * WaveFormat.BlockAlign);
            _buffer.AddSamples(bytes[^length..]);
        }
    }
    public void Clear() { lock (_gate) _buffer.ClearBuffer(); }
    public int Read(Span<byte> buffer) { lock (_gate) return _buffer.Read(buffer); }
}
