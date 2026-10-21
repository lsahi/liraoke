using System.Windows;
using System.Windows.Media;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Infrastructure.Rendering;

/// <summary>Shared preview/export painter. FFT work is completed before this method is called.</summary>
public sealed class WpfAudioVisualizationPainter
{
    private static readonly Brush Blue = FrozenBrush(Color.FromArgb(205, 142, 211, 239));
    private static readonly Brush Yellow = FrozenBrush(Color.FromArgb(205, 255, 222, 136));
    private static readonly Pen Edge = FrozenPen();
    private static readonly Brush White = FrozenBrush(Color.FromArgb(155, 255, 255, 255));
    private static readonly Brush FaintWhite = FrozenBrush(Color.FromArgb(96, 255, 255, 255));

    public void Draw(DrawingContext context, AudioVisualizationData? data, ProjectTime time, double width, double height,
        AudioVisualizationStyle style = AudioVisualizationStyle.Classic)
    {
        if (data is null || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) { return; }
        if (time.TotalSeconds >= data.DurationSeconds) { return; }
        if (!style.IsAvailable()) { return; }
        Span<float> bands = stackalloc float[AudioVisualizationData.SpectrumBandCount];
        data.Sample(time, bands);
        if (style == AudioVisualizationStyle.RotatingRing)
        { DrawRing(context, bands, time, width, height); return; }
        if (style is AudioVisualizationStyle.BottomDense or AudioVisualizationStyle.CenterWhite)
        { DrawDense(context, bands, width, height, style == AudioVisualizationStyle.CenterWhite); return; }
        double spectrumWidth = width * 0.52;
        double slot = spectrumWidth / bands.Length;
        double barWidth = slot * 0.62;
        // Anchor the original blue/yellow bars to the lower-left of the video itself.
        // Use the short side for both inset and height so portrait frames do not
        // produce disproportionately tall bars. Subtitles are drawn afterwards.
        double shortSide = Math.Min(width, height);
        double inset = shortSide * 0.025;
        double left = inset;
        double bottom = height - inset;
        double maximumHeight = shortSide * 0.20;
        double radius = Math.Max(0.5, barWidth / 2);
        for (int band = 0; band < bands.Length; band++)
        {
            double barHeight = maximumHeight * bands[band];
            if (barHeight < 0.75) { continue; }
            var rectangle = new Rect(left + band * slot + (slot - barWidth) / 2, bottom - barHeight, barWidth, barHeight);
            context.DrawRoundedRectangle(band % 4 == 3 ? Yellow : Blue, Edge, rectangle, radius, radius);
        }
    }

    private static void DrawDense(DrawingContext context, ReadOnlySpan<float> bands, double width, double height, bool centered)
    {
        int count = centered ? AudioVisualizationMotion.CenterBarCount : AudioVisualizationMotion.DenseBarCount;
        double shortSide = Math.Min(width, height), slot = width / count;
        double barWidth = slot * (centered ? 0.50 : 0.72), baseline = centered ? height / 2 : height;
        // C uses viewport height: both halves together reach 50%, including portrait formats.
        double maximum = centered ? height * 0.25 : shortSide * 0.22;
        for (int i = 0; i < count; i++)
        {
            double length = maximum * AudioVisualizationMotion.DenseBand(bands, i, count);
            if (length < 0.5) { continue; }
            context.DrawRectangle(centered ? FaintWhite : i % 4 == 3 ? Yellow : Blue, null,
                new Rect(i * slot + (slot - barWidth) / 2, baseline - length, barWidth, length * (centered ? 2 : 1)));
        }
    }

    private static void DrawRing(DrawingContext context, ReadOnlySpan<float> bands, ProjectTime time, double width, double height)
    {
        // Inner radius grows another 10% over 0.4.4.1; keep the outward bars unchanged.
        // Full outer diameter is now 71.68% of the short side.
        // Short-side scaling keeps the enlarged circle intact in portrait frames.
        double shortSide = Math.Min(width, height), radius = shortSide * 0.16 * 1.15 * 1.10, maximum = shortSide * 0.12 * 1.30;
        double barWidth = 2 * Math.PI * radius / AudioVisualizationMotion.DenseBarCount * 0.65;
        context.PushTransform(new TranslateTransform(width / 2, height / 2));
        context.PushTransform(new RotateTransform(AudioVisualizationMotion.RotationDegrees(time)));
        for (int i = 0; i < AudioVisualizationMotion.DenseBarCount; i++)
        {
            // Mirror the frequency axis around the circle, avoiding a low/high discontinuity at the seam.
            int mirrored = i < 64 ? i : 127 - i;
            double length = maximum * AudioVisualizationMotion.DenseBand(bands, mirrored, 64);
            if (length < 0.5) { continue; }
            context.PushTransform(new RotateTransform(i * 360.0 / AudioVisualizationMotion.DenseBarCount));
            context.DrawRoundedRectangle(White, null,
                new Rect(-barWidth / 2, -radius - length, barWidth, length), barWidth / 2, barWidth / 2);
            context.Pop();
        }
        context.Pop(); context.Pop();
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen()
    {
        var pen = new Pen(FrozenBrush(Color.FromArgb(90, 38, 71, 100)), 0.6);
        pen.Freeze();
        return pen;
    }
}
