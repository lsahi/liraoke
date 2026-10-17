using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Application.Rendering;

/// <summary>Named output choices; projects continue to persist their explicit pixel dimensions.</summary>
public static class VideoOutputFormats
{
    public static IReadOnlyList<string> AspectRatios { get; } = Array.AsReadOnly<string>(["16:9", "4:3", "1:1", "9:16"]);
    public static IReadOnlyList<int> ResolutionStandards { get; } = Array.AsReadOnly<int>([1080, 720]);

    public static (int Width, int Height) GetDimensions(string aspectRatio, int resolution)
    {
        if (resolution is not (720 or 1080))
        {
            throw new ArgumentOutOfRangeException(nameof(resolution), "Resolution must be 720p or 1080p.");
        }

        // The resolution names describe the short edge, including square and portrait output.
        return aspectRatio switch
        {
            "16:9" => (resolution * 16 / 9, resolution),
            "4:3" => (resolution * 4 / 3, resolution),
            "1:1" => (resolution, resolution),
            "9:16" => (resolution, resolution * 16 / 9),
            _ => throw new ArgumentOutOfRangeException(nameof(aspectRatio), "Unsupported aspect ratio."),
        };
    }

    public static ExportPreset ApplyTo(ExportPreset current, string aspectRatio, int resolution)
    {
        ArgumentNullException.ThrowIfNull(current);
        (int width, int height) = GetDimensions(aspectRatio, resolution);
        return current with { Width = width, Height = height };
    }

    public static bool TryIdentify(int width, int height, out string aspectRatio, out int resolution)
    {
        foreach (string ratio in AspectRatios)
        {
            foreach (int standard in ResolutionStandards)
            {
                if (GetDimensions(ratio, standard) == (width, height))
                {
                    aspectRatio = ratio;
                    resolution = standard;
                    return true;
                }
            }
        }

        // Do not silently reinterpret a custom/legacy size as a supported preset.
        aspectRatio = string.Empty;
        resolution = 0;
        return false;
    }
}
