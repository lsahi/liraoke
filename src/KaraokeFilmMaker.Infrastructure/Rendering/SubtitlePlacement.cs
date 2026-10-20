using System.Globalization;
using System.Windows;
using System.Windows.Media;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Infrastructure.Rendering;

// Font-driven, bottom-anchored rows. SafeAreaRatio is retained only in project files for compatibility.
internal sealed record SubtitlePlacement(double Margin, double BottomPadding, double RowHeight, double RowGap, double MaxTextHeight)
{
    public double Y(SubtitleSlot slot, double textHeight, double canvasHeight) =>
        canvasHeight - BottomPadding - textHeight - (slot == SubtitleSlot.TopLeft ? RowHeight + RowGap : 0);

    public static SubtitlePlacement Create(SubtitleStyle style, double width, double height)
    {
        double scale = Math.Min(width, height) / 1080;
        double margin = Math.Max(12 * scale, width * style.MarginRatio);
        double padding = Math.Max(margin * 0.25, (style.OutlineWidth + style.ShadowOffset + 2) * scale);
        double gap = Math.Max(8 * scale, Math.Max(margin * 0.25, (2 * style.OutlineWidth + style.ShadowOffset) * scale));
        double maxHeight = Math.Max(1, (height - padding * 2 - gap) / 2);
        var face = new Typeface(new FontFamily(SystemFontCatalog.Resolve(style.FontFamily)), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        double Measure(double size) => new FormattedText("国Agあ", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, face, Math.Max(1, size * scale), Brushes.White, 1).Height;
        // Reserve the auxiliary row consistently, including Japanese lines without kanji.
        double rowHeight = Math.Min(maxHeight, Measure(style.PrimaryFontSize) + Measure(style.AnnotationFontSize) + style.LineGap * scale);
        return new(margin, padding, rowHeight, gap, maxHeight);
    }
}
