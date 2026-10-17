using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Audio;
using KaraokeFilmMaker.Infrastructure.Media;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class WasapiPlaybackTests
{
    [Fact]
    public async Task SilentAudioUsesDeviceClockAndSupportsPauseAndSeek()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KaraokeFilmMaker.AudioTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "silent.wav");
        try
        {
            var tools = MediaToolchain.Find();
            await MediaProcess.RunAsync(tools.FfmpegPath, ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i",
                "anullsrc=r=48000:cl=stereo", "-t", "2", file]);
            await using var playback = new ScaledAudioPlaybackSession();
            await playback.LoadPreparedAsync(file, ProjectDuration.FromSeconds(2), 1);
            await playback.PlayAsync();
            await Task.Delay(180);
            await playback.PauseAsync();
            ProjectTime stopped = playback.Position;
            Assert.True(stopped.Microseconds > 0);
            await Task.Delay(80);
            Assert.InRange(Math.Abs(playback.Position.Microseconds - stopped.Microseconds), 0, 20_000);
            await playback.SeekAsync(ProjectTime.FromSeconds(1));
            Assert.InRange(playback.Position.TotalSeconds, 0.99, 1.01);
            await playback.PlayAsync();
            await Task.Delay(100);
            await playback.PauseAsync();
            Assert.True(playback.Position.TotalSeconds > 1);
            // A tempo-processed file may be a few samples shorter than the project.
            await playback.LoadPreparedAsync(file, ProjectDuration.FromSeconds(1.01), 0.5);
            await playback.SeekAsync(ProjectTime.FromSeconds(1.01));
            Assert.Equal(1.01, playback.Position.TotalSeconds, precision: 5);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
