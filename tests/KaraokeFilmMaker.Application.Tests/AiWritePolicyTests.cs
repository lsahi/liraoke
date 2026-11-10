using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
namespace KaraokeFilmMaker.Application.Tests;
public class AiWritePolicyTests
{
    [Fact]public void AppendIsAllowedButReplacementDeletionAndOtherEditsAreRejected()
    {
        var asset=new MediaAsset(Guid.NewGuid(),"song","song.wav",MediaKind.Audio,ProjectDuration.FromSeconds(5),"hash");
        var p=KaraokeProject.Create("song",DateTimeOffset.UtcNow) with {MediaAssets=[asset],MasterAudioAssetId=asset.Id};
        var added=new MediaAsset(Guid.NewGuid(),"new","new.png",MediaKind.Image,ProjectDuration.Zero,"imagehash");
        Assert.True(AiProjectWritePolicy.OnlyAddedMedia(p,p with {MediaAssets=[asset,added]}));
        Assert.False(AiProjectWritePolicy.OnlyAddedMedia(p,p with {MediaAssets=[added]}));
        Assert.False(AiProjectWritePolicy.OnlyAddedMedia(p,p with {MediaAssets=[asset with {DisplayName="edited"}]}));
        Assert.False(AiProjectWritePolicy.OnlyAddedMedia(p,p with {MasterAudioAssetId=added.Id,MediaAssets=[asset,added]}));
        Assert.False(AiProjectWritePolicy.OnlyAddedMedia(p,p with {Title="changed"}));
    }
}
