using KaraokeFilmMaker.Application.Music;
using KaraokeFilmMaker.Infrastructure.Music;
namespace KaraokeFilmMaker.Infrastructure.Tests;
public sealed class FirstPlayableMusicTests
{
    private static MusicTrack Track(int id)=>new("netease",id.ToString(),"Song "+id,"Artist","",60);
    [Fact] public async Task SkipsUnavailableAndStopsAtFirstValidatedAudio()
    {
        int called=0;using var expected=new PreparedMusicAudio("","",Track(2),"standard",60,false,"仅试听");
        var result=await FirstPlayableMusic.FindAsync([Track(1),Track(2),Track(3)],(track,_)=>{called++;return track.Id=="1"?Task.FromException<PreparedMusicAudio>(new InvalidOperationException("unavailable")):Task.FromResult(expected);},default);
        Assert.Same(expected,result.Audio);Assert.Equal(2,called);Assert.Equal(2,result.CheckedCount);
    }
    [Fact] public async Task LimitsProbesAndHonorsCancellation()
    {
        var result=await FirstPlayableMusic.FindAsync(Enumerable.Range(1,30).Select(Track),(_,_)=>Task.FromException<PreparedMusicAudio>(new IOException()),default);
        Assert.Null(result.Audio);Assert.Equal(10,result.CheckedCount);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>FirstPlayableMusic.FindAsync([Track(1)],(_,_)=>throw new Exception("must not probe"),cancel.Token));
    }
}
