using KaraokeFilmMaker.Application.Music;
namespace KaraokeFilmMaker.Application.Tests;
public sealed class MusicSourceRegistryTests
{
    [Fact] public async Task AggregateKeepsVersionsAndIsolatesProviderFailure()
    {
        var registry = new MusicSourceRegistry([new Fake("a"), new Fake("b"), new Fake("broken", true)]);
        var result = await registry.SearchAsync("test", null, _ => new());
        Assert.Equal(4, result.Tracks.Count); Assert.Single(result.Warnings);
        Assert.Equal(2, result.Tracks.Count(x => x.Title == "song (live)"));
    }
    [Fact] public async Task CancellationIsNotReportedAsNoResults()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MusicSourceRegistry([new Fake("a")]).SearchAsync("x", null, _ => new(), cancellation.Token));
    }
    private sealed class Fake(string id, bool fail = false) : IMusicSourceAdapter
    {
        public MusicSourceDescriptor Descriptor => new(id,id,MusicSourceCapabilities.Search,MediaAuthenticationKind.None);
        public Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, MusicSourceCredentials credentials, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); if(fail) throw new IOException("private-provider-detail");
            return Task.FromResult<IReadOnlyList<MusicTrack>>([new(id,"1","song (live)","artist","album",10),new(id,"2","song (remix)","artist","album",11)]);
        }
        public Task<ResolvedMusicAudio> ResolveAsync(MusicTrack track, MusicSourceCredentials credentials, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
