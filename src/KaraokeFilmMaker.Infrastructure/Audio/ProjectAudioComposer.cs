using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Workspaces;

namespace KaraokeFilmMaker.Infrastructure.Audio;

public sealed class ProjectAudioComposer(MediaToolchain tools)
{
    public async Task<string> PrepareAsync(string workspacePath, KaraokeProject project, double rate = 1,
        CancellationToken cancellationToken = default)
    {
        if (rate is not (0.5 or 0.75 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(rate));
        }

        var master = project.MediaAssets.FirstOrDefault(asset => asset.Id == project.MasterAudioAssetId)
            ?? throw new InvalidOperationException("请先导入主歌曲音轨。");
        string masterPath = WorkspacePaths.AssetPath(workspacePath, master);
        var reader = new FfprobeMediaReader(tools);
        if (!(await reader.ReadDetailsAsync(masterPath, cancellationToken).ConfigureAwait(false)).HasAudio)
        {
            throw new InvalidOperationException("主歌曲素材没有音轨，请导入 MP3 或 WAV 歌曲。");
        }

        var enabled = project.VideoClips.Where(clip => !clip.IsMuted
            && project.MediaAssets.Any(asset => asset.Id == clip.MediaAssetId && asset.Kind == MediaKind.Video)
            && clip.TimelineStart.Microseconds < master.Duration.Microseconds
            && !(clip.MediaAssetId == master.Id && clip.TimelineStart == clip.SourceStart))
            .OrderBy(clip => clip.TimelineStart).ToArray();
        var assets = enabled.Select(clip => project.MediaAssets.First(asset => asset.Id == clip.MediaAssetId)).ToArray();
        var identity = new
        {
            Master = master,
            MasterWrite = File.GetLastWriteTimeUtc(masterPath),
            Clips = enabled,
            Files = assets.Select(asset => new { Asset = asset, Write = File.GetLastWriteTimeUtc(WorkspacePaths.AssetPath(workspacePath, asset)) }),
            Rate = rate,
        };
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity))));
        string directory = Path.Combine(workspacePath, "cache", "audio");
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, $"mix-{hash}.wav");
        if (File.Exists(output) && new FileInfo(output).Length > 44)
        {
            return output;
        }

        string temporary = Path.Combine(directory, $"mix-{hash}-{Guid.NewGuid():N}.partial.wav");
        List<string> arguments = ["-nostdin", "-v", "error", "-y", "-i", masterPath];
        var filters = new StringBuilder();
        string duration = Number(master.Duration.TotalSeconds);
        filters.Append($"[0:a]atrim=duration={duration},asetpts=PTS-STARTPTS,aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,apad,atrim=duration={duration}[master];");
        List<string> labels = ["[master]"];
        int input = 1;
        for (var index = 0; index < enabled.Length; index++)
        {
            var clip = enabled[index];
            string source = WorkspacePaths.AssetPath(workspacePath, assets[index]);
            if (!(await reader.ReadDetailsAsync(source, cancellationToken).ConfigureAwait(false)).HasAudio)
            {
                continue;
            }

            arguments.AddRange(["-i", source]);
            double clippedDuration = Math.Min(clip.Duration.TotalSeconds, master.Duration.TotalSeconds - clip.TimelineStart.TotalSeconds);
            filters.Append($"[{input}:a]atrim=start={Number(clip.SourceStart.TotalSeconds)}:duration={Number(clippedDuration)},asetpts=PTS-STARTPTS,aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,adelay={clip.TimelineStart.Microseconds / 1000}:all=1[clip{input}];");
            labels.Add($"[clip{input}]");
            input++;
        }

        filters.Append(string.Concat(labels));
        filters.Append(labels.Count > 1 ? $"amix=inputs={labels.Count}:duration=first:normalize=0," : "anull,");
        filters.Append($"alimiter=limit=0.95:latency=1,apad,atrim=duration={duration},atempo={Number(rate)}[mix]");
        arguments.AddRange(["-filter_complex_threads", "1", "-filter_complex", filters.ToString(), "-map", "[mix]",
            "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2", temporary]);
        try
        {
            await MediaProcess.RunAsync(tools.FfmpegPath, arguments, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, output, overwrite: true);
            return output;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
