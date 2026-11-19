using System.Globalization;
using KaraokeFilmMaker.Infrastructure.Media;

namespace KaraokeFilmMaker.Infrastructure.Audio;

public static class PracticeCover
{
    public static string PathFor(string root, Guid source) => Path.Combine(root, "recordings", source.ToString("N") + ".flac");

    public static Task<string> MixAsync(string backing, string voice, string output, double start, double end,
        int offsetMs, double duration, CancellationToken token, double gainDb = 0)
    {
        static string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        double position = start + offsetMs / 1000d;
        long delay = (long)Math.Round(Math.Max(0, position) * 48000);
        string filter = $"[0:a]aresample=48000,apad,atrim=duration={N(duration)}[song];" +
            $"[1:a]atrim=start={N(Math.Max(0,-position))}:end={N(end-start)},asetpts=PTS-STARTPTS,aresample=48000,volume={N(gainDb)}dB,adelay={delay}S:all=1[v];" +
            "[song][v]amix=inputs=2:duration=first:normalize=0,alimiter=limit=0.97:latency=1,aformat=channel_layouts=stereo[a]";
        return MediaProcess.RunAsync(MediaToolchain.Find().FfmpegPath, ["-nostdin","-v","error","-y","-i",backing,"-i",voice,
            "-filter_complex",filter,"-map","[a]","-metadata",$"take_start={N(start)}","-metadata",$"take_end={N(end)}",
            "-metadata",$"vocal_offset_ms={offsetMs}","-metadata",$"vocal_gain_db={N(gainDb)}",output],token);
    }
}
