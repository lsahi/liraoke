using System.Text.Json;
using NAudio.Wave;

namespace KaraokeFilmMaker.Infrastructure.Audio;

public sealed record WaveformData(double DurationSeconds, int BinsPerSecond, float[] Peaks);

public sealed class WaveformCache
{
    public async Task<WaveformData> GetAsync(string wavePath, string workspacePath, CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(workspacePath, "cache", "waveforms");
        Directory.CreateDirectory(directory);
        string cache = Path.Combine(directory, Path.GetFileNameWithoutExtension(wavePath) + ".json");
        if (File.Exists(cache))
        {
            try
            {
                await using FileStream file = File.OpenRead(cache);
                var loaded = await JsonSerializer.DeserializeAsync<WaveformData>(file, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (loaded is not null && loaded.BinsPerSecond > 0 && loaded.Peaks is not null)
                {
                    return loaded;
                }
            }
            catch (JsonException) { }
        }

        WaveformData data = await Task.Run(() =>
        {
            using var reader = new WaveFileReader(wavePath);
            var samples = reader.ToSampleProvider();
            const int binsPerSecond = 100;
            float[] buffer = new float[Math.Max(1, reader.WaveFormat.SampleRate / binsPerSecond) * reader.WaveFormat.Channels];
            List<float> peaks = [];
            int count;
            while ((count = samples.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                float peak = 0;
                for (var index = 0; index < count; index++)
                {
                    peak = Math.Max(peak, Math.Abs(buffer[index]));
                }

                peaks.Add(peak);
            }

            return new WaveformData(reader.TotalTime.TotalSeconds, binsPerSecond, peaks.ToArray());
        }, cancellationToken).ConfigureAwait(false);
        string temporary = cache + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(data), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, cache, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) { File.Delete(temporary); }
        }

        return data;
    }
}
