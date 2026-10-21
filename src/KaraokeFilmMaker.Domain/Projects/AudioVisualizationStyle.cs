namespace KaraokeFilmMaker.Domain.Projects;

public enum AudioVisualizationStyle
{
    Classic = 0,
    BottomDense = 1,
    CenterWhite = 2,
    RotatingRing = 3,
    // Reserved for lossless reading of pre-0.5 projects. No longer selectable or rendered.
    Fireworks = 4,
}

public static class AudioVisualizationStyles
{
    public static bool IsAvailable(this AudioVisualizationStyle style) => style is
        AudioVisualizationStyle.Classic or AudioVisualizationStyle.BottomDense or
        AudioVisualizationStyle.CenterWhite or AudioVisualizationStyle.RotatingRing;
}
