namespace KaraokeFilmMaker.Infrastructure.Media;

public sealed record MediaToolchain(string FfmpegPath, string FfprobePath)
{
    public static MediaToolchain Find()
    {
        foreach (string initial in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            DirectoryInfo? directory = new(initial);
            for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            {
                string bin = Path.Combine(directory.FullName, "tools", "ffmpeg", "ffmpeg-8.1.2-essentials_build", "bin");
                string ffmpeg = Path.Combine(bin, "ffmpeg.exe");
                string ffprobe = Path.Combine(bin, "ffprobe.exe");
                if (File.Exists(ffmpeg) && File.Exists(ffprobe))
                {
                    return new MediaToolchain(ffmpeg, ffprobe);
                }
            }
        }

        throw new FileNotFoundException("未找到媒体工具。请先运行项目中的 scripts/bootstrap-media.ps1。");
    }
}
