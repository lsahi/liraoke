using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Application.Media;

public sealed record MediaRemovalImpact(MediaAsset Asset, bool IsMasterAudio, int VideoClipCount)
{
    public bool RequiresConfirmation => IsMasterAudio || VideoClipCount > 0;
}

/// <summary>Only changes project references. Workspace media files are retained for undo and recovery.</summary>
public static class MediaAssetRemoval
{
    public static MediaRemovalImpact Inspect(KaraokeProject project, Guid assetId)
    {
        MediaAsset asset = project.MediaAssets.SingleOrDefault(item => item.Id == assetId)
            ?? throw new ArgumentException("素材已经不在当前项目中。", nameof(assetId));
        return new(asset, project.MasterAudioAssetId == assetId, project.VideoClips.Count(clip => clip.MediaAssetId == assetId));
    }

    public static KaraokeProject Remove(KaraokeProject project, Guid assetId, bool confirmReferencedRemoval = false)
    {
        MediaRemovalImpact impact = Inspect(project, assetId);
        if (impact.RequiresConfirmation && !confirmReferencedRemoval)
        { throw new InvalidOperationException("此素材正在用于歌曲或视频轨道，需先确认移除关联内容。"); }
        return project with
        {
            MediaAssets = project.MediaAssets.Where(asset => asset.Id != assetId).ToArray(),
            Accompaniments = project.Accompaniments.Where(item => item.SourceAssetId != assetId).ToArray(),
            VideoClips = project.VideoClips.Where(clip => clip.MediaAssetId != assetId).ToArray(),
            MasterAudioAssetId = impact.IsMasterAudio ? null : project.MasterAudioAssetId,
            WorkArea = impact.IsMasterAudio ? null : project.WorkArea,
            ModifiedAtUtc = DateTimeOffset.UtcNow,
        };
    }
}
