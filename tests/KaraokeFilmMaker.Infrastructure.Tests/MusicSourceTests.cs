using System.Net;
using System.Net.Http;
using KaraokeFilmMaker.Application.Music;
using KaraokeFilmMaker.Infrastructure.Music;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Infrastructure.Workspaces;
using KaraokeFilmMaker.Domain.Media;

namespace KaraokeFilmMaker.Infrastructure.Tests;
public sealed class MusicSourceTests
{
    [Fact] public void CredentialsAreEncryptedAndPreserveAiSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new UserPreferenceStore(root); store.SaveAiProvider(Guid.NewGuid(),"test",true,null,new("ai-test-secret"));
            store.SaveMusicAccount("qishui",new("sessionid=music-test-secret","http://127.0.0.1:8899/sign"));
            string text=File.ReadAllText(store.FilePath);Assert.DoesNotContain("music-test-secret",text);Assert.DoesNotContain("ai-test-secret",text);
            Assert.Equal("sessionid=music-test-secret",UserPreferenceStore.ReadMusicAccount(Assert.Single(store.Load().MusicAccounts)).Cookie);
            Assert.Single(store.Load().AiProviders);
            Assert.Throws<InvalidDataException>(()=>store.SaveMusicAccount("qishui",new("x","http://remote.example/sign")));
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }
    [Theory][InlineData(30,false,false)][InlineData(200,false,true)][InlineData(200,true,false)][InlineData(250,false,false)]
    public void CompleteAudioRequiresMatchingDurationAndNoTrial(double seconds,bool trial,bool expected) =>
        Assert.Equal(expected,MusicAudioTransfer.IsComplete(new("netease","1","x","a","b",200),seconds,trial));
    [Theory][InlineData("127.0.0.1")][InlineData("169.254.169.254")][InlineData("10.1.1.1")][InlineData("::1")][InlineData("::ffff:192.168.1.2")]
    public void RejectsPrivateDownloadDestinations(string ip) => Assert.False(PublicMediaHttp.IsPublicAddress(IPAddress.Parse(ip)));
    [Fact] public async Task GeneratedAudioCanBePreparedImportedAndMovedWithoutNetwork()
    {
        string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var tools=MediaToolchain.Find();string input=Path.Combine(root,"fixture.wav");
            await MediaProcess.RunAsync(tools.FfmpegPath,["-nostdin","-v","error","-f","lavfi","-i","sine=frequency=440:duration=4","-c:a","pcm_s16le",input]);
            var bytes=await File.ReadAllBytesAsync(input); using var client=new HttpClient(new AudioHandler(bytes));
            var service=new MusicAudioTransfer(tools,new MusicHostClient(root),client);var track=new MusicTrack("netease","1","検証 (Test)","test","test",4);
            using var audio=await service.PrepareAsync(new Fake(),track,new());
            Assert.True(audio.CanImport);Assert.InRange(audio.DurationSeconds,3.95,4.05);
            var workspace=new WorkspaceService(new JsonProjectRepository(),new FfprobeMediaReader(tools));
            var session=await workspace.CreateAsync(Path.Combine(root,"project"),"test");
            session=await workspace.ImportMediaAsync(session,audio.AudioPath,MediaKind.Audio);await workspace.SaveAsync(session);
            string imported=Assert.Single(session.Project.MediaAssets).RelativePath;Assert.False(Path.IsPathRooted(imported));
            string moved=Path.Combine(root,"moved");Directory.Move(session.RootPath,moved);
            var opened=await workspace.OpenAsync(moved);Assert.True(File.Exists(Path.Combine(opened.RootPath,imported)));
            string stage=audio.DirectoryPath;audio.Dispose();Assert.False(Directory.Exists(stage));
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }
    [Fact] public async Task CancellationAndInvalidPayloadDoNotLeaveStagingFiles()
    {
        using var client=new HttpClient(new AudioHandler([1,2,3]));var tools=MediaToolchain.Find();
        string stageRoot=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        var service=new MusicAudioTransfer(tools,new MusicHostClient(),client,stageRoot);
        var track=new MusicTrack("netease","1","bad","a","b",4);
        await Assert.ThrowsAnyAsync<Exception>(()=>service.PrepareAsync(new Fake(),track,new()));
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.PrepareAsync(new Fake(),track,new(),cancellationToken:cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(stageRoot)); Directory.Delete(stageRoot);
    }
    private sealed class AudioHandler(byte[] bytes) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
      { token.ThrowIfCancellationRequested();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)}); } }
    private sealed class Fake : IMusicSourceAdapter
    {
        public MusicSourceDescriptor Descriptor=>new("netease","test",MusicSourceCapabilities.ImportAudio,MediaAuthenticationKind.None);
        public Task<IReadOnlyList<MusicTrack>> SearchAsync(string query,MusicSourceCredentials credentials,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<ResolvedMusicAudio> ResolveAsync(MusicTrack track,MusicSourceCredentials credentials,CancellationToken cancellationToken=default)
        { cancellationToken.ThrowIfCancellationRequested();return Task.FromResult(new ResolvedMusicAudio(new Uri("https://cdn.example.com/fixture"),"standard")); }
    }
}
