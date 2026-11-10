using KaraokeFilmMaker.Application.Music;

namespace KaraokeFilmMaker.Infrastructure.Music;

public static class FirstPlayableMusic
{
    public static async Task<(PreparedMusicAudio? Audio, int CheckedCount)> FindAsync(
        IEnumerable<MusicTrack> tracks, Func<MusicTrack, CancellationToken, Task<PreparedMusicAudio>> prepare, CancellationToken token)
    {
        int count = 0;
        foreach (var track in tracks.Take(10))
        {
            token.ThrowIfCancellationRequested(); count++;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            attempt.CancelAfter(TimeSpan.FromSeconds(35));
            try
            {
                var audio = await prepare(track, attempt.Token).ConfigureAwait(false);
                if (token.IsCancellationRequested) { audio.Dispose(); token.ThrowIfCancellationRequested(); }
                return (audio, count);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is InvalidOperationException or IOException or System.Net.Http.HttpRequestException or OperationCanceledException) { }
        }
        return (null, count);
    }
}
