using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;

namespace KaraokeFilmMaker.Application.Timeline;

public static class RecordingMediaPreview
{
    // The recording monitor follows the song source, not unrelated background video edits.
    public static VideoClip? At(KaraokeProject project, ProjectTime position)
    {
        var source = project.MediaAssets.FirstOrDefault(asset => asset.Id == project.MasterAudioAssetId);
        return source is { Kind: MediaKind.Video } && position < ProjectTime.Zero + source.Duration
            ? new VideoClip(source.Id, source.Id, ProjectTime.Zero, ProjectTime.Zero, source.Duration, true)
            : null;
    }
}
