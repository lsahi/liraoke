using KaraokeFilmMaker.Application.Separation;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Infrastructure.Media;

namespace KaraokeFilmMaker.Infrastructure.Alignment;

/// <summary>Creates a vocal-only inference input without changing the song clock or project media.</summary>
public sealed class AlignmentAudioPreparation(IVocalSeparationService separation, MediaToolchain tools)
{
    public async Task<string> PrepareAsync(string source, string job, SeparationEngine engine, int gpuIndex,
        IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(job);
        string scratch = Path.Combine(job, "vocals-" + Guid.NewGuid().ToString("N"));
        string wav = Path.Combine(job, "audio.wav");
        if (File.Exists(wav)) throw new IOException("打轴输入文件已存在，请创建新的任务目录。");
        Directory.CreateDirectory(scratch);
        try
        {
            progress?.Report($"提取人声（Demucs / {engine}）…");
            string vocals = await separation.SeparateAsync(new(source, scratch, engine, SeparationOutput.Vocals, gpuIndex),
                new ForwardProgress<SeparationProgress>(p => progress?.Report("人声提取：" + p.Message +
                    (p.Fraction is {} fraction ? $" {fraction:P0}" : ""))), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            progress?.Report("人声提取完成，转换为 16 kHz 单声道；保留原歌曲时间基准…");
            await MediaProcess.RunAsync(tools.FfmpegPath,
                ["-v", "error", "-nostdin", "-i", vocals, "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_f32le", wav], token).ConfigureAwait(false);
            var reader = new FfprobeMediaReader(tools);
            var original = await reader.ReadAsync(source, MediaKind.Audio, token).ConfigureAwait(false);
            var prepared = await reader.ReadAsync(wav, MediaKind.Audio, token).ConfigureAwait(false);
            if (prepared.Duration.TotalSeconds <= 0 || Math.Abs(original.Duration.TotalSeconds - prepared.Duration.TotalSeconds) > .1)
                throw new InvalidDataException("人声输入与原歌曲时长不一致，已停止打轴。");
            token.ThrowIfCancellationRequested();
            return wav;
        }
        catch
        {
            if (File.Exists(wav)) File.Delete(wav);
            throw;
        }
        finally
        {
            // Only this task's generated scratch directory is removed. Source/project files are never touched.
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
    }
}

// Deliberately synchronous: all progress/log writes finish before the next worker opens the log.
internal sealed class ForwardProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
