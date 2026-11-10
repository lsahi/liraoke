using System.Text.Json;
using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Application.Ai;
public static class AiProjectWritePolicy
{
    public static bool OnlyAddedMedia(KaraokeProject before,KaraokeProject current)
    {
        if(before.MediaAssets.Any(a=>!current.MediaAssets.Contains(a))||current.MediaAssets.Select(a=>a.Id).Distinct().Count()!=current.MediaAssets.Count)return false;
        return JsonSerializer.Serialize(before with {MediaAssets=current.MediaAssets,ModifiedAtUtc=DateTimeOffset.MinValue})==JsonSerializer.Serialize(current with {ModifiedAtUtc=DateTimeOffset.MinValue});
    }
}
