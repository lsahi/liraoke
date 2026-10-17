using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Infrastructure.Media;

namespace KaraokeFilmMaker.Infrastructure.Audio;

/// <summary>
/// Offline, bounded spectrum analysis of the original song. Only quantized spectrum frames are
/// cached; decoded PCM is streamed, never retained as a full song or decoded during playback.
/// </summary>
public sealed class AudioVisualizationCache(MediaToolchain tools)
{
    public const int AnalysisSampleRate = 11025;
    private const int FftSize = 1024;
    private const int HeaderSize = 32;
    private const int CacheVersion = 1;
    private const uint CacheMagic = 0x5652414C;
    private const int Bands = AudioVisualizationData.SpectrumBandCount;
    private const int FramesPerSecond = AudioVisualizationData.SpectrumFramesPerSecond;

    public async Task<AudioVisualizationData> GetAsync(string sourcePath, string workspacePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        cancellationToken.ThrowIfCancellationRequested();
        var source = new FileInfo(Path.GetFullPath(sourcePath));
        if (!source.Exists) { throw new FileNotFoundException("找不到用于音频可视化的歌曲素材。", source.FullName); }
        long sourceLength = source.Length;
        long sourceModified = source.LastWriteTimeUtc.Ticks;
        string signature = $"spectrum-{CacheVersion}|{source.FullName}|{sourceLength}|{sourceModified}";
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
        string directory = Path.Combine(workspacePath, "cache", "audio-visualization");
        Directory.CreateDirectory(directory);
        string cachePath = Path.Combine(directory, key + ".spectrum");
        AudioVisualizationData? cached = await TryReadCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);
        if (cached is not null) { return cached; }

        string temporary = cachePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await Task.Run(() => AnalyzeAsync(source.FullName, temporary, cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            source.Refresh();
            if (!source.Exists || source.Length != sourceLength || source.LastWriteTimeUtc.Ticks != sourceModified)
            {
                throw new IOException("分析期间歌曲素材发生了变化，请重试音频可视化。");
            }
            File.Move(temporary, cachePath, overwrite: true);
            return await TryReadCacheAsync(cachePath, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("生成的音频可视化缓存无效，请重新尝试。");
        }
        finally
        {
            if (File.Exists(temporary)) { File.Delete(temporary); }
        }
    }

    private async Task AnalyzeAsync(string sourcePath, string outputPath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(tools.FfmpegPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in new[]
        {
            "-nostdin", "-v", "error", "-threads", "1", "-i", sourcePath,
            "-t", (AudioVisualizationData.MaximumDurationSeconds + 1).ToString(CultureInfo.InvariantCulture),
            "-map", "0:a:0", "-vn", "-ac", "1", "-ar", AnalysisSampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "s16le", "-c:a", "pcm_s16le", "pipe:1",
        }) { start.ArgumentList.Add(argument); }

        using var process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        using CancellationTokenRegistration registration = cancellationToken.Register(() => Kill(process));
        Task<string> errors = ReadErrorsAsync(process.StandardError);
        try
        {
            await AnalyzePcmAsync(process.StandardOutput.BaseStream, outputPath, cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string diagnostic = await errors.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"无法分析歌曲音频（{process.ExitCode}）：{diagnostic}");
            }
        }
        catch
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static async Task AnalyzePcmAsync(Stream pcm, string outputPath, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 32768, true);
        await output.WriteAsync(new byte[HeaderSize], cancellationToken).ConfigureAwait(false);
        var samples = new float[FftSize];
        var real = new float[FftSize];
        var imaginary = new float[FftSize];
        var hann = new float[FftSize];
        var smoothed = new float[Bands];
        var frame = new byte[Bands];
        var bytes = new byte[FftSize * 2];
        var lowerBins = new int[Bands];
        var upperBins = new int[Bands];
        for (int index = 0; index < FftSize; index++) { hann[index] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (FftSize - 1))); }
        for (int band = 0; band < Bands; band++)
        {
            double low = 40 * Math.Pow(125, (double)band / Bands);
            double high = 40 * Math.Pow(125, (double)(band + 1) / Bands);
            lowerBins[band] = Math.Clamp((int)Math.Floor(low * FftSize / AnalysisSampleRate), 1, FftSize / 2 - 1);
            upperBins[band] = Math.Clamp((int)Math.Ceiling(high * FftSize / AnalysisSampleRate), lowerBins[band] + 1, FftSize / 2);
        }

        int read = await ReadSamplesAsync(pcm, samples, FftSize / 2, FftSize / 2, bytes, cancellationToken).ConfigureAwait(false);
        long sampleCount = read;
        bool ended = read < FftSize / 2;
        int frameCount = 0;
        long centerSample = 0;
        while (sampleCount > centerSample)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frameCount >= AudioVisualizationData.MaximumFrameCount ||
                sampleCount > (long)AudioVisualizationData.MaximumDurationSeconds * AnalysisSampleRate)
            {
                throw new InvalidOperationException("音频可视化目前支持最长 4 小时的歌曲；可以关闭此开关继续播放、打轴和导出。");
            }
            for (int index = 0; index < FftSize; index++) { real[index] = samples[index] * hann[index]; imaginary[index] = 0; }
            Transform(real, imaginary);
            for (int band = 0; band < Bands; band++)
            {
                double peak = 0;
                for (int bin = lowerBins[band]; bin < upperBins[band]; bin++)
                {
                    double magnitude = Math.Sqrt(real[bin] * real[bin] + imaginary[bin] * imaginary[bin]) * 4 / FftSize;
                    peak = Math.Max(peak, magnitude);
                }
                // Fixed -55..0 dB scaling is stable across seeking and cache rebuilds; silence stays transparent.
                float value = (float)Math.Clamp((20 * Math.Log10(Math.Max(peak, 1e-9)) + 55) / 55, 0, 1);
                smoothed[band] = Math.Max(value, smoothed[band] * 0.72f);
                frame[band] = (byte)Math.Clamp((int)Math.Round(smoothed[band] * 255), 0, 255);
            }
            await output.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            frameCount++;
            long nextCenter = (long)frameCount * AnalysisSampleRate / FramesPerSecond;
            int shift = (int)(nextCenter - centerSample);
            Array.Copy(samples, shift, samples, 0, FftSize - shift);
            Array.Clear(samples, FftSize - shift, shift);
            if (!ended)
            {
                read = await ReadSamplesAsync(pcm, samples, FftSize - shift, shift, bytes, cancellationToken).ConfigureAwait(false);
                sampleCount += read;
                ended = read < shift;
            }
            centerSample = nextCenter;
        }

        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, CacheMagic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), CacheVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), FramesPerSecond);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), Bands);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), frameCount);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(20), (double)sampleCount / AnalysisSampleRate);
        output.Position = 0;
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadSamplesAsync(Stream source, float[] target, int offset, int count, byte[] buffer, CancellationToken cancellationToken)
    {
        int length = await source.ReadAtLeastAsync(buffer.AsMemory(0, count * 2), count * 2, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (length % 2 != 0) { throw new InvalidDataException("歌曲音频解码结果不完整。"); }
        for (int sample = 0; sample < length / 2; sample++)
        {
            target[offset + sample] = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(sample * 2, 2)) / 32768f;
        }
        return length / 2;
    }

    private static void Transform(float[] real, float[] imaginary)
    {
        for (int index = 1, reversed = 0; index < real.Length; index++)
        {
            int bit = real.Length >> 1;
            while ((reversed & bit) != 0) { reversed ^= bit; bit >>= 1; }
            reversed ^= bit;
            if (index < reversed) { (real[index], real[reversed]) = (real[reversed], real[index]); }
        }
        for (int size = 2; size <= real.Length; size <<= 1)
        {
            double angle = -2 * Math.PI / size;
            float incrementReal = (float)Math.Cos(angle), incrementImaginary = (float)Math.Sin(angle);
            for (int start = 0; start < real.Length; start += size)
            {
                float weightReal = 1, weightImaginary = 0;
                for (int half = 0; half < size / 2; half++)
                {
                    int even = start + half, odd = even + size / 2;
                    float oddReal = real[odd] * weightReal - imaginary[odd] * weightImaginary;
                    float oddImaginary = real[odd] * weightImaginary + imaginary[odd] * weightReal;
                    real[odd] = real[even] - oddReal; imaginary[odd] = imaginary[even] - oddImaginary;
                    real[even] += oddReal; imaginary[even] += oddImaginary;
                    float nextReal = weightReal * incrementReal - weightImaginary * incrementImaginary;
                    weightImaginary = weightReal * incrementImaginary + weightImaginary * incrementReal;
                    weightReal = nextReal;
                }
            }
        }
    }

    private static async Task<AudioVisualizationData?> TryReadCacheAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) { return null; }
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32768, true);
            if (file.Length < HeaderSize || file.Length > HeaderSize + AudioVisualizationData.MaximumFrameCount * Bands) { return null; }
            var header = new byte[HeaderSize];
            await file.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            int frames = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CacheMagic ||
                BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)) != CacheVersion ||
                BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) != FramesPerSecond ||
                BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12)) != Bands ||
                frames < 0 || frames > AudioVisualizationData.MaximumFrameCount || file.Length != HeaderSize + (long)frames * Bands)
            { return null; }
            double duration = BinaryPrimitives.ReadDoubleLittleEndian(header.AsSpan(20));
            if (!double.IsFinite(duration) || duration < 0 || duration > AudioVisualizationData.MaximumDurationSeconds) { return null; }
            var payload = new byte[frames * Bands];
            await file.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            return new AudioVisualizationData(duration, payload);
        }
        catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static async Task<string> ReadErrorsAsync(StreamReader reader)
    {
        var errors = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (errors.Length < 16000) { errors.Append(buffer, 0, Math.Min(count, 16000 - errors.Length)); }
        }
        return errors.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
