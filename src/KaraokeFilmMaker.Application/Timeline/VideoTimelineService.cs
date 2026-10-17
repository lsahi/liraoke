using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;

namespace KaraokeFilmMaker.Application.Timeline;

public sealed class VideoTimelineService
{
    public static ProjectDuration DefaultImageDuration => ProjectDuration.FromSeconds(5);

    public KaraokeProject UpdateClip(
        KaraokeProject project,
        Guid clipId,
        ProjectTime timelineStart,
        ProjectTime sourceStart,
        ProjectDuration duration,
        bool isMuted,
        int? trackIndex = null)
    {
        VideoClip clip = FindClip(project, clipId);
        int track = trackIndex ?? clip.TrackIndex;
        if (track != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackIndex), "当前版本只支持一条视频轨道。");
        }
        ValidateSourceRange(project, clip.MediaAssetId, sourceStart, duration);
        VideoClip updated = clip with
        {
            TimelineStart = timelineStart,
            SourceStart = sourceStart,
            Duration = duration,
            IsMuted = isMuted,
            TrackIndex = track,
        };
        VideoClip[] clips = project.VideoClips
            .Select(item => item.Id == clipId ? updated : item)
            .OrderBy(item => item.TimelineStart)
            .ToArray();
        EnsureNoOverlap(clips);
        return project with { VideoClips = clips, ModifiedAtUtc = DateTimeOffset.UtcNow };
    }

    public KaraokeProject RemoveClip(KaraokeProject project, Guid clipId)
    {
        _ = FindClip(project, clipId);
        return project with
        {
            VideoClips = project.VideoClips.Where(clip => clip.Id != clipId).ToArray(),
            ModifiedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public KaraokeProject DuplicateClip(KaraokeProject project, Guid clipId)
    {
        VideoClip source = FindClip(project, clipId);
        ProjectTime start = project.VideoClips.Where(clip => clip.TrackIndex == source.TrackIndex)
            .MaxBy(clip => clip.TimelineEnd)!.TimelineEnd;
        VideoClip copy = source with { Id = Guid.NewGuid(), TimelineStart = start };
        return project with
        {
            VideoClips = [.. project.VideoClips.OrderBy(clip => clip.TimelineStart), copy],
            ModifiedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public KaraokeProject JoinSequentially(KaraokeProject project, IReadOnlyList<Guid> orderedClipIds)
    {
        if (orderedClipIds.Count != project.VideoClips.Count
            || orderedClipIds.Distinct().Count() != project.VideoClips.Count)
        {
            throw new ArgumentException("拼接顺序必须恰好包含时间轴中的全部片段。", nameof(orderedClipIds));
        }

        Dictionary<Guid, VideoClip> byId = project.VideoClips.ToDictionary(clip => clip.Id);
        List<VideoClip> joined = [];
        ProjectTime cursor = ProjectTime.Zero;
        foreach (Guid id in orderedClipIds)
        {
            if (!byId.TryGetValue(id, out VideoClip? clip))
            {
                throw new ArgumentException("拼接顺序包含未知片段。", nameof(orderedClipIds));
            }

            joined.Add(clip with { TimelineStart = cursor, TrackIndex = 0 });
            cursor += clip.Duration;
        }

        return project with { VideoClips = joined, ModifiedAtUtc = DateTimeOffset.UtcNow };
    }

    public KaraokeProject SplitClip(KaraokeProject project, Guid clipId, ProjectTime position)
    {
        VideoClip clip = FindClip(project, clipId);
        if (position <= clip.TimelineStart || position >= clip.TimelineEnd)
        {
            throw new InvalidOperationException("播放头必须位于选中片段内部才能分割。");
        }
        ProjectDuration firstLength = position - clip.TimelineStart;
        bool isImage = project.MediaAssets.First(item => item.Id == clip.MediaAssetId).Kind == MediaKind.Image;
        VideoClip first = clip with { Duration = firstLength };
        VideoClip second = clip with { Id = Guid.NewGuid(), TimelineStart = position,
            SourceStart = isImage ? ProjectTime.Zero : clip.SourceStart + firstLength,
            Duration = new ProjectDuration(clip.Duration.Microseconds - firstLength.Microseconds) };
        return project with { VideoClips = project.VideoClips.Select(item => item.Id == clip.Id ? first : item)
            .Append(second).OrderBy(item => item.TimelineStart).ToArray(), ModifiedAtUtc = DateTimeOffset.UtcNow };
    }

    public static VideoClip? VisibleClipAt(KaraokeProject project, ProjectTime position) =>
        project.VideoClips.Where(clip => clip.TimelineStart <= position && position < clip.TimelineEnd)
            .OrderByDescending(clip => clip.TrackIndex).FirstOrDefault();

    private static VideoClip FindClip(KaraokeProject project, Guid clipId) =>
        project.VideoClips.FirstOrDefault(clip => clip.Id == clipId)
        ?? throw new ArgumentException("未找到指定的视频片段。", nameof(clipId));

    private static void ValidateSourceRange(
        KaraokeProject project,
        Guid mediaAssetId,
        ProjectTime sourceStart,
        ProjectDuration duration)
    {
        if (duration.Microseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "片段时长必须大于零。 ");
        }

        MediaAsset asset = project.MediaAssets.FirstOrDefault(item => item.Id == mediaAssetId)
            ?? throw new InvalidOperationException("视频片段引用的素材不存在。");
        if (asset.Kind == MediaKind.Image)
        {
            if (sourceStart != ProjectTime.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceStart), "图片片段的源起点必须为零，请调整显示时长。");
            }
            return;
        }
        if (asset.Kind != MediaKind.Video || sourceStart + duration > ProjectTime.Zero + asset.Duration)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "裁切范围超出了视频素材时长。");
        }
    }

    private static void EnsureNoOverlap(IReadOnlyList<VideoClip> clips)
    {
        foreach (var track in clips.GroupBy(clip => clip.TrackIndex))
        {
            VideoClip[] ordered = track.OrderBy(clip => clip.TimelineStart).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                if (ordered[index].TimelineStart < ordered[index - 1].TimelineEnd)
                {
                    throw new InvalidOperationException("视频片段不能重叠。请调整位置或裁切片段。");
                }
            }
        }
    }
}
