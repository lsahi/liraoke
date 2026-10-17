using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Timing;
using KaraokeFilmMaker.Infrastructure.Media;
using NAudio.Wave;

namespace KaraokeFilmMaker.Infrastructure.Audio;

/// <summary>Bounded, CPU-only constant-tempo estimate. No network, models, or changes to source media.</summary>
public sealed class OfflineTempoAnalysisService(MediaToolchain tools) : ITempoAnalysisService
{
    public const int MaximumAnalysisSeconds = 90;
    public const int AnalysisSampleRate = 11025;

    public async Task<TempoAnalysisResult> AnalyzeAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(sourcePath)) { throw new FileNotFoundException("找不到待分析的歌曲素材。", sourcePath); }

        // The output is bounded to < 2 MB. A private temporary path is never shared with the project.
        string temporary = Path.Combine(Path.GetTempPath(), $"Liraoke-tempo-{Guid.NewGuid():N}.wav");
        try
        {
            await MediaProcess.RunAsync(tools.FfmpegPath,
                ["-nostdin", "-v", "error", "-y", "-threads", "1", "-i", sourcePath,
                 "-t", MaximumAnalysisSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                 "-map", "0:a:0", "-vn", "-ac", "1", "-ar", AnalysisSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                 "-c:a", "pcm_s16le", temporary], cancellationToken).ConfigureAwait(false);

            return await Task.Run(() =>
            {
                using var reader = new WaveFileReader(temporary);
                var provider = reader.ToSampleProvider();
                var samples = new float[MaximumAnalysisSeconds * AnalysisSampleRate];
                int count = 0;
                while (count < samples.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = provider.Read(samples.AsSpan(count, Math.Min(8192, samples.Length - count)));
                    if (read == 0) { break; }
                    count += read;
                }

                return ConstantTempoEstimator.Estimate(samples.AsSpan(0, count), AnalysisSampleRate, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temporary)) { File.Delete(temporary); }
        }
    }
}
