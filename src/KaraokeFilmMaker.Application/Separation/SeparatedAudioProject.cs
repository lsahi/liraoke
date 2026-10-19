using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Application.Separation;

public static class SeparatedAudioProject
{
    public static KaraokeProject Apply(KaraokeProject original, KaraokeProject imported, Guid sourceId, bool useAsSong)
    {
        var additions = imported.MediaAssets.Where(a => original.MediaAssets.All(old => old.Id != a.Id)).ToArray();
        if (additions.Length != 1) throw new InvalidOperationException("分离结果必须恰好包含一个新增音频素材。");
        var source = original.MediaAssets.Single(a => a.Id == sourceId);
        var result = additions[0];
        if (Math.Abs(source.Duration.TotalSeconds - result.Duration.TotalSeconds) > .1)
            throw new InvalidOperationException("分离音频时长不匹配，拒绝替换歌曲。");
        // The worker pads only the trailing codec delay to the original media clock.
        // Retain the exact microsecond master duration so crop/timing never changes.
        var assets = imported.MediaAssets.Select(a => a.Id == result.Id ? a with { Duration = source.Duration } : a).ToArray();
        return original with
        {
            MediaAssets = assets,
            MasterAudioAssetId = useAsSong ? result.Id : original.MasterAudioAssetId,
            VideoClips = useAsSong ? original.VideoClips.Select(c => c.MediaAssetId == sourceId ? c with { IsMuted = true } : c).ToArray() : original.VideoClips,
            ModifiedAtUtc = DateTimeOffset.UtcNow,
        };
    }
}
