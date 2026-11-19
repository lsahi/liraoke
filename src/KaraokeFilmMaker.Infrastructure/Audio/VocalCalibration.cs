using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KaraokeFilmMaker.Infrastructure.Audio;

public static class VocalCalibration
{
    public static async Task<double> MeasureAsync(string? inputId, Action<double> showLevel, CancellationToken token)
    {
        using var devices = new MMDeviceEnumerator();
        using var input = inputId == null ? devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) : devices.GetDevice(inputId);
        await using var capture = new WasapiRecorderBuilder().WithDevice(input).WithSharedMode().WithEventSync().WithBufferLength(20).Build();
        var buffer = new BufferedWaveProvider(capture.WaveFormat, TimeSpan.FromSeconds(1)) { ReadFully = false };
        var source = buffer.ToSampleProvider();
        float[] samples = new float[2048];
        var gate = new object();
        float recent = 0, peak = 0;
        Exception? failure = null;
        capture.RecordingStopped += (_, args) => Volatile.Write(ref failure, args.Exception);
        capture.DataAvailable += (data, flags, _, _) =>
        {
            if ((flags & AudioClientBufferFlags.Silent) != 0) return;
            buffer.AddSamples(data);
            int count;
            float packet = 0;
            while ((count = source.Read(samples)) > 0)
                for (int i = 0; i < count; i++) packet = Math.Max(packet, Math.Abs(samples[i]));
            lock (gate) { peak = Math.Max(peak, packet); recent = Math.Max(recent, packet); }
        };
        capture.StartRecording();
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(100, token);
            if (Volatile.Read(ref failure) is { } error) throw new IOException("试唱输入中断。", error);
            float level;
            lock (gate) { level = recent; recent = 0; }
            showLevel(level);
        }
        lock (gate) return peak;
    }
}
