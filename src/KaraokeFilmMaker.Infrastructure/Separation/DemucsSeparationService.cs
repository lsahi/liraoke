using System.Text.Json;
using KaraokeFilmMaker.Application.Separation;
using KaraokeFilmMaker.Infrastructure.Media;

namespace KaraokeFilmMaker.Infrastructure.Separation;

public sealed class DemucsSeparationService(DemucsRuntimeManager runtime, MediaToolchain mediaTools) : IVocalSeparationService
{
    public async Task<string> SeparateAsync(SeparationRequest request, IProgress<SeparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!runtime.CpuInstalled) throw new InvalidOperationException("未找到 CPU 分离组件。请使用包含分离组件的完整安装包。");
        if (!File.Exists(request.SourcePath)) throw new FileNotFoundException("歌曲文件不存在。", request.SourcePath);
        using var operation = runtime.AcquireOperationLock();
        Directory.CreateDirectory(request.OutputDirectory);
        string taskRoot = Path.Combine(request.OutputDirectory, "separation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(taskRoot);
        string decoded = Path.Combine(taskRoot, "input.wav");
        string name = Path.GetFileNameWithoutExtension(request.SourcePath);
        if (name.Length > 80) name = name[..80];
        string suffix = request.Output switch { SeparationOutput.Vocals => "人声", SeparationOutput.SubtractVocals => "伴奏B", _ => "伴奏A" };
        string output = Path.Combine(taskRoot, $"{name}-{suffix}.wav");
        bool complete = false;
        try
        {
            var metadata = await new FfprobeMediaReader(mediaTools).ReadAsync(request.SourcePath,
                KaraokeFilmMaker.Domain.Media.MediaKind.Audio, cancellationToken).ConfigureAwait(false);
            if (metadata.Duration.TotalSeconds <= 0 || metadata.Duration.TotalSeconds > 1800)
                throw new InvalidOperationException("首版人声分离支持最长 30 分钟的歌曲，以限制内存占用。");
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(taskRoot))!);
            if (drive.AvailableFreeSpace < metadata.Duration.TotalSeconds * 44100 * 2 * 14 + 256 * 1024 * 1024)
                throw new IOException("工作区磁盘空间不足，无法生成分离缓存和音频。");
            progress?.Report(new("解码歌曲（原文件不变）…", 0));
            await MediaProcess.RunAsync(mediaTools.FfmpegPath,
                ["-nostdin", "-v", "error", "-i", request.SourcePath, "-map", "0:a:0", "-vn", "-ac", "2", "-ar", "44100", "-af", "apad",
                    "-t", metadata.Duration.TotalSeconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture), "-c:a", "pcm_f32le", decoded],
                cancellationToken).ConfigureAwait(false);
            async Task Run(SeparationEngine engine)
            {
                string path = engine == SeparationEngine.Gpu ? runtime.InstalledGpuPath
                    ?? throw new InvalidOperationException("GPU 加速尚未安装。") : runtime.CpuPath;
                var args = runtime.WorkerArguments(engine, request.GpuIndex);
                args.AddRange(["--input", decoded, "--output", output, "--mode", request.Output switch
                { SeparationOutput.SubtractVocals => "subtract", SeparationOutput.Vocals => "vocals", _ => "sum" }]);
                await MediaProcess.RunAsync(Path.Combine(path, "python.exe"), args, cancellationToken, outputLine: line =>
                {
                    try
                    {
                        var update = JsonSerializer.Deserialize<SeparationProgress>(line,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (update is not null) progress?.Report(update);
                    }
                    catch (JsonException) { }
                }).ConfigureAwait(false);
            }
            try { await Run(request.Engine).ConfigureAwait(false); }
            catch (Exception e) when (request.Engine == SeparationEngine.Gpu && !cancellationToken.IsCancellationRequested
                && e is not OutOfMemoryException)
            {
                // Never reuse partial GPU audio or silently change existing project media.
                if (File.Exists(output)) File.Delete(output);
                progress?.Report(new("GPU 处理失败，正在回退 CPU。可稍后修复 GPU 组件。"));
                await Run(SeparationEngine.Cpu).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = await new FfprobeMediaReader(mediaTools).ReadAsync(output,
                KaraokeFilmMaker.Domain.Media.MediaKind.Audio, cancellationToken).ConfigureAwait(false);
            if (Math.Abs(result.Duration.TotalSeconds - metadata.Duration.TotalSeconds) > .1)
                throw new InvalidDataException("生成音频与原歌曲时长不一致，已拒绝应用。");
            complete = true;
            return output;
        }
        finally
        {
            if (complete) File.Delete(decoded);
            else if (Directory.Exists(taskRoot)) Directory.Delete(taskRoot, true);
        }
    }
}
