using System.Windows;
using System.Windows.Media;

namespace KaraokeFilmMaker.Infrastructure.Rendering;

/// <summary>Isolates a timed text range before stroking it, so adjacent unsung glyphs stay untouched.</summary>
internal sealed class WpfSubtitleHighlightPainter
{
    private readonly Dictionary<(Geometry, Rect), Geometry> _ranges = [];

    public void Draw(DrawingContext context, Geometry geometry, Rect bounds, double fillWidth, Brush fill, Pen outline,
        bool isolateRange = true)
    {
        if (fillWidth <= 0 || bounds.IsEmpty || geometry.IsEmpty()) { return; }
        Geometry part = geometry;
        if (isolateRange)
        {
            var key = (geometry, bounds);
            if (!_ranges.TryGetValue(key, out part!))
            {
                // Character bounds include advance width; isolate horizontally, but retain accents vertically.
                var mask = new RectangleGeometry(new Rect(bounds.X, geometry.Bounds.Y - 1, bounds.Width, geometry.Bounds.Height + 2));
                part = Geometry.Combine(geometry, mask, GeometryCombineMode.Intersect, null);
                part.Freeze();
                if (_ranges.Count >= 1024) { _ranges.Clear(); }
                _ranges[key] = part;
            }
        }
        double pad = outline.Thickness / 2 + 1;
        double right = fillWidth >= bounds.Width ? Math.Max(bounds.Right, part.Bounds.Right) + pad : bounds.Left + fillWidth;
        double left = Math.Min(bounds.Left, part.Bounds.Left) - pad;
        context.PushClip(new RectangleGeometry(new Rect(left, part.Bounds.Top - pad,
            Math.Max(0, right - left), part.Bounds.Height + 2 * pad)));
        context.DrawGeometry(outline.Brush, outline, part);
        context.DrawGeometry(fill, null, part);
        context.Pop();
    }
}
