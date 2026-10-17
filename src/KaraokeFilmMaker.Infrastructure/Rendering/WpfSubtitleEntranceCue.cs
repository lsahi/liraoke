using System.Windows;
using System.Windows.Media;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Infrastructure.Rendering;

/// <summary>Three descending bars fill in song time before an entrance; never adds lyric units.</summary>
internal static class WpfSubtitleEntranceCue
{
    public static double ReservedWidth(SubtitleLineScene scene, SubtitleStyle style, double scale) =>
        scene.EntranceProgress.HasValue ? style.PrimaryFontSize * scale * 0.75 : 0;

    // Drawn to the left of the primary text's local origin. Retain the reserved space while singing
    // so the line and its ruby do not jump or change size when the cue disappears.
    public static void Draw(DrawingContext context, SubtitleLineScene scene, double width, double primaryTop,
        double primaryHeight, Brush inactive, Brush active, Pen outline, Pen? activeOutline)
    {
        if (scene.EntranceProgress is not { } progress || progress >= 1 || width <= 0) { return; }
        double barWidth = width * 0.13, step = width * 0.25;
        double bottom = primaryTop + primaryHeight * 0.82;
        for (int index = 0; index < 3; index++)
        {
            double height = primaryHeight * (0.62 - index * 0.15);
            var rect = new Rect(-width + index * step + barWidth / 2, bottom - height, barWidth, height);
            double radius = barWidth / 2;
            context.DrawRoundedRectangle(inactive, outline, rect, radius, radius);
            double filled = Math.Clamp(progress * 3 - index, 0, 1);
            if (filled <= 0) { continue; }
            double pad = Math.Max(outline.Thickness, activeOutline?.Thickness ?? 0) + 1;
            context.PushClip(new RectangleGeometry(new Rect(rect.Left - pad, rect.Bottom - height * filled - (filled >= 1 ? pad : 0),
                rect.Width + 2 * pad, height * filled + 2 * pad)));
            context.DrawRoundedRectangle(active, activeOutline ?? outline, rect, radius, radius);
            context.Pop();
        }
    }
}
