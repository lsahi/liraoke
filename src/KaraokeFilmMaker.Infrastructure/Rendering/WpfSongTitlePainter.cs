using System.Globalization;
using System.Windows;
using System.Windows.Media;
using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Infrastructure.Rendering;

public sealed class WpfSongTitlePainter
{
    private (SongTitleSettings Settings, string Text, double Width, double Height)? _key;
    private Geometry? _geometry;
    private Pen? _outline;
    private Brush? _fill;
    private double _shadow;
    public void Draw(DrawingContext context, KaraokeProject project, double width, double height)
    {
        var settings = project.SongTitle;
        if (!settings.Enabled) return;
        string text = settings.ResolveText(project);
        if (!settings.Enabled || string.IsNullOrWhiteSpace(text) || width <= 0 || height <= 0) return;
        if (_key != (settings, text, width, height))
        {
            double scale = Math.Min(width, height) / 1080;
            double margin = Math.Max(12 * scale, width * settings.MarginRatio);
            var face = new Typeface(new FontFamily(SystemFontCatalog.Resolve(settings.FontFamily)), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            FormattedText Format(double size) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                face, Math.Max(1, size), Brushes.White, 1);
            var formatted = Format(settings.FontSize * scale);
            double fit = Math.Min(1, Math.Min((width - 2 * margin) / Math.Max(1, formatted.WidthIncludingTrailingWhitespace),
                height * .25 / Math.Max(1, formatted.Height)));
            formatted = Format(settings.FontSize * scale * fit);
            double x = settings.Position == SongTitlePosition.TopRight ? width - margin - formatted.WidthIncludingTrailingWhitespace : margin;
            _geometry = formatted.BuildGeometry(new Point(x, Math.Max(12 * scale, height * settings.MarginRatio)));
            _geometry.Freeze();
            _fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(settings.Color)); _fill.Freeze();
            _outline = new Pen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(settings.OutlineColor)), settings.OutlineWidth * scale);
            _outline.Freeze(); _shadow = settings.ShadowOffset * scale;
            _key = (settings, text, width, height);
        }
        context.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
        if (_shadow > 0)
        {
            context.PushTransform(new TranslateTransform(_shadow, _shadow));
            context.DrawGeometry(Brushes.Black, null, _geometry); context.Pop();
        }
        context.DrawGeometry(_fill, _outline, _geometry);
        context.Pop();
    }
}
