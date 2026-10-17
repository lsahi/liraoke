using System.Text;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Validation;
using KaraokeFilmMaker.Infrastructure.Audio;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Rendering;
using KaraokeFilmMaker.Infrastructure.Workspaces;

namespace KaraokeFilmMaker.Infrastructure.Export;

public sealed record ExportProgress(double Fraction, string Message);

public sealed class VideoExportService(MediaToolchain tools)
{
    public async Task ExportAsync(string workspacePath, KaraokeProject project, string outputPath,
        IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> errors = ProjectValidator.Validate(project);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        ExportPreset preset = project.ExportPreset;
        if (preset.Width % 2 != 0 || preset.Height % 2 != 0)
        {
            throw new InvalidOperationException("H.264 输出的宽高必须是偶数。");
        }

        outputPath = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(outputPath), ".mp4", StringComparison.OrdinalIgnoreCase)
            || project.MediaAssets.Any(asset => string.Equals(WorkspacePaths.AssetPath(workspacePath, asset), outputPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("输出必须是 MP4 文件，并且不能覆盖项目素材。");
        }

        var master = project.MediaAssets.FirstOrDefault(asset => asset.Id == project.MasterAudioAssetId)
            ?? throw new InvalidOperationException("项目没有主歌曲。");
        TimeRange area = project.GetWorkArea();
        double seconds = area.Duration.TotalSeconds;
        if (seconds <= 0)
        {
            throw new InvalidOperationException("歌曲时长必须大于零。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string temporary = Path.Combine(Path.GetDirectoryName(outputPath)!, $".{Path.GetFileNameWithoutExtension(outputPath)}-{Guid.NewGuid():N}.partial.mp4");
        progress?.Report(new ExportProgress(0, "准备主歌曲与视频原声混音"));
        string audio = await new ProjectAudioComposer(tools).PrepareAsync(workspacePath, project, cancellationToken: cancellationToken).ConfigureAwait(false);
        AudioVisualizationData? visualization = null;
        if (project.AudioVisualizationEnabled)
        {
            progress?.Report(new ExportProgress(0.04, "准备原歌曲频谱缓存"));
            visualization = await new AudioVisualizationCache(tools).GetAsync(
                WorkspacePaths.AssetPath(workspacePath, master), workspacePath, cancellationToken).ConfigureAwait(false);
        }
        List<string> args = ["-v", "error", "-y", "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", $"{preset.Width}x{preset.Height}",
            "-framerate", preset.FramesPerSecond.ToString(), "-i", "pipe:0",
            "-ss", ProjectAudioComposer.Number(area.Start.TotalSeconds), "-i", audio];
        // Intersect each background clip with the work area, then move only the export copy to zero.
        // Lyrics and visualization keep the original project clock in the frame renderer below.
        var clips = project.VideoClips.Where(clip => clip.TimelineStart < area.End && clip.TimelineEnd > area.Start)
            .Select(clip =>
            {
                var start = clip.TimelineStart > area.Start ? clip.TimelineStart : area.Start;
                var end = clip.TimelineEnd < area.End ? clip.TimelineEnd : area.End;
                bool image = project.MediaAssets.First(item => item.Id == clip.MediaAssetId).Kind == MediaKind.Image;
                return clip with
                {
                    TimelineStart = new ProjectTime(start.Microseconds - area.Start.Microseconds),
                    SourceStart = image ? ProjectTime.Zero : new ProjectTime(clip.SourceStart.Microseconds + start.Microseconds - clip.TimelineStart.Microseconds),
                    Duration = new ProjectDuration(end.Microseconds - start.Microseconds),
                };
            })
            .OrderBy(clip => clip.TrackIndex).ThenBy(clip => clip.TimelineStart).ToArray();
        for (var index = 0; index < clips.Length; index++)
        {
            var clip = clips[index];
            var asset = project.MediaAssets.First(item => item.Id == clip.MediaAssetId);
            if (asset.Kind == MediaKind.Image)
            {
                VideoPreviewFiles image = await new ImagePreviewCache(tools).PrepareAsync(workspacePath, asset, cancellationToken).ConfigureAwait(false);
                args.AddRange(["-loop", "1", "-framerate", preset.FramesPerSecond.ToString(),
                    "-t", ProjectAudioComposer.Number(Math.Min(clip.Duration.TotalSeconds, seconds - clip.TimelineStart.TotalSeconds)),
                    "-i", image.ProxyPath]);
            }
            else
            {
                args.AddRange(["-i", WorkspacePaths.AssetPath(workspacePath, asset)]);
            }
        }

        var graph = new StringBuilder($"color=c=black:s={preset.Width}x{preset.Height}:r={preset.FramesPerSecond}:d={ProjectAudioComposer.Number(seconds)}[bg0];");
        for (var index = 0; index < clips.Length; index++)
        {
            var clip = clips[index];
            var asset = project.MediaAssets.First(item => item.Id == clip.MediaAssetId);
            string start = ProjectAudioComposer.Number(clip.TimelineStart.TotalSeconds);
            string end = ProjectAudioComposer.Number(Math.Min(seconds, clip.TimelineEnd.TotalSeconds));
            graph.Append($"[{index + 2}:v]trim=start={ProjectAudioComposer.Number(clip.SourceStart.TotalSeconds)}:duration={ProjectAudioComposer.Number(clip.Duration.TotalSeconds)},setpts=PTS-STARTPTS+{start}/TB,");
            if (asset.Kind == MediaKind.Image)
            {
                // Match the preview's UniformToFill: preserve proportions, cover the frame, and crop symmetrically.
                graph.Append($"scale={preset.Width}:{preset.Height}:force_original_aspect_ratio=increase,crop={preset.Width}:{preset.Height}:(iw-ow)/2:(ih-oh)/2,");
            }
            else
            {
                graph.Append($"scale={preset.Width}:{preset.Height}:force_original_aspect_ratio=decrease,pad={preset.Width}:{preset.Height}:(ow-iw)/2:(oh-ih)/2:color=black,");
            }
            graph.Append($"setsar=1,fps={preset.FramesPerSecond}[v{index}];");
            graph.Append($"[bg{index}][v{index}]overlay=eof_action=pass:repeatlast=0:enable='gte(t,{start})*lt(t,{end})'[bg{index + 1}];");
        }

        graph.Append($"[bg{clips.Length}][0:v]overlay=alpha=straight:eof_action=pass:repeatlast=0,format=yuv420p[out]");
        args.AddRange(["-filter_complex_threads", "1", "-filter_complex", graph.ToString(), "-map", "[out]", "-map", "1:a:0",
            "-c:v", "libx264", "-preset", "fast", "-b:v", $"{preset.VideoBitrateKbps}k", "-threads", Math.Clamp(Environment.ProcessorCount - 1, 1, 8).ToString(),
            "-c:a", "aac", "-b:a", $"{preset.AudioBitrateKbps}k", "-t", ProjectAudioComposer.Number(seconds),
            "-movflags", "+faststart", "-progress", "pipe:1", temporary]);
        try
        {
            await using var renderer = new WpfSubtitleFrameRenderer(project, visualization);
            long count = (long)Math.Ceiling((decimal)area.Duration.Microseconds * preset.FramesPerSecond / 1_000_000);
            await MediaProcess.RunAsync(tools.FfmpegPath, args, cancellationToken, async (stream, token) =>
            {
                for (long frame = 0; frame < count; frame++)
                {
                    token.ThrowIfCancellationRequested();
                    var time = new ProjectTime(area.Start.Microseconds + (long)((decimal)frame * 1_000_000 / preset.FramesPerSecond));
                    RenderedFrame image = await renderer.RenderAsync(time, preset.Width, preset.Height, token).ConfigureAwait(false);
                    await stream.WriteAsync(image.BgraPixels, token).ConfigureAwait(false);
                    if (frame % preset.FramesPerSecond == 0)
                    {
                        progress?.Report(new ExportProgress(0.1 + 0.85 * frame / count, "合成背景、歌曲与字幕"));
                    }
                }
            }).ConfigureAwait(false);
            progress?.Report(new ExportProgress(0.98, "检查输出视频"));
            MediaDetails details = await new FfprobeMediaReader(tools).ReadDetailsAsync(temporary, cancellationToken).ConfigureAwait(false);
            if (details.Width != preset.Width || details.Height != preset.Height || !details.HasAudio
                || Math.Abs((details.FrameRate ?? 0) - preset.FramesPerSecond) > 0.01
                || Math.Abs(details.Duration.TotalSeconds - seconds) > Math.Max(0.2, 2d / preset.FramesPerSecond))
            {
                throw new InvalidDataException("编码结果的分辨率、帧率、音轨或时长不符合项目设置。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, outputPath, overwrite: true);
            progress?.Report(new ExportProgress(1, "导出完成"));
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
