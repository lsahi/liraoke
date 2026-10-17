using System.Globalization;
using System.Windows;
using System.Windows.Media;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Infrastructure.Rendering;

/// <summary>Shared vector layout and drawing for preview and offscreen frames.</summary>
public sealed class WpfSubtitlePainter
{
    private readonly Dictionary<LayoutKey, TextLayout> _layouts = [];
    private readonly Dictionary<string, Brush> _brushes = [];
    private readonly WpfJapaneseSubtitlePainter _japanese = new();
    private readonly WpfSubtitleHighlightPainter _highlightPainter = new();

    public void Draw(DrawingContext context, SubtitleScene scene, SubtitleStyle style, double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double scale = Math.Min(width, height) / 1080;
        double margin = Math.Max(12 * scale, width * style.MarginRatio);
        double regionTop = height * (1 - style.SafeAreaRatio);
        double slotHeight = height * style.SafeAreaRatio / 2;
        Brush inactive = ColorBrush(style.InactiveColor);
        Brush active = ColorBrush(style.ActiveColor);
        Brush outline = ColorBrush(style.OutlineColor);
        var outlinePen = new Pen(outline, style.OutlineWidth * scale) { LineJoin = PenLineJoin.Round };
        var activeOutlinePen = style.ActiveOutlineColor is null ? null
            : new Pen(ColorBrush(style.ActiveOutlineColor), style.OutlineWidth * scale) { LineJoin = PenLineJoin.Round };

        context.PushClip(new RectangleGeometry(new Rect(0, regionTop, width, height - regionTop)));
        foreach (SubtitleLineScene item in scene.Lines)
        {
            bool hasSnapshot = LyricPresentationLayout.HasCurrentSnapshot(item.Line);
            string annotation = Annotation(item.Line);
            // Explicit whole-line pinyin/translation remains one auxiliary line. It must not be
            // repeated for each language span or silently disappear behind a cached ruby layout.
            bool wholeLineAnnotation = hasSnapshot && annotation.Length > 0
                && (item.Line.AnnotationType is AnnotationType.Pinyin or AnnotationType.Translation
                    || (item.Line.AnnotationType is AnnotationType.Kana or AnnotationType.Romaji) && IsNonJapanese(item));
            if (hasSnapshot ? !wholeLineAnnotation : item.Language == LyricLanguage.Japanese)
            {
                _japanese.Draw(context, item, style, width, height);
                continue;
            }
            var key = new LayoutKey(item.Line.PrimaryText, annotation, style.FontFamily,
                style.PrimaryFontSize * scale, style.AnnotationFontSize * scale,
                Math.Max(1, width - 2 * margin - WpfSubtitleEntranceCue.ReservedWidth(item, style, scale)), slotHeight - margin * 0.5, style.LineGap * scale);
            TextLayout layout = GetLayout(key);
            double cueWidth = WpfSubtitleEntranceCue.ReservedWidth(item, style, scale);
            double x = item.Slot == SubtitleSlot.TopLeft ? margin + cueWidth : width - margin - layout.Width;
            double y = item.Slot == SubtitleSlot.TopLeft
                ? regionTop + margin * 0.25
                : height - margin * 0.25 - layout.Height;

            context.PushTransform(new TranslateTransform(x, y));
            WpfSubtitleEntranceCue.Draw(context, item, cueWidth, layout.PrimaryTop, layout.PrimaryText.Height,
                inactive, active, outlinePen, activeOutlinePen);
            DrawText(context, layout.PrimaryGeometry, inactive, outlinePen, style.ShadowOffset * scale);
            if (layout.AnnotationGeometry is not null)
            {
                DrawText(context, layout.AnnotationGeometry, inactive, outlinePen, style.ShadowOffset * scale);
            }

            foreach (SubtitleHighlight highlight in item.Highlights.Where(value => value.Progress > 0))
            {
                Rect[] bounds = highlight.DisplayRanges
                    .Where(range => range.Start >= 0 && range.EndExclusive <= item.Line.PrimaryText.Length)
                    .Select(range => layout.PrimaryText.BuildHighlightGeometry(
                        new Point(0, layout.PrimaryTop), range.Start, range.Length)?.Bounds ?? Rect.Empty)
                    .Where(rect => !rect.IsEmpty && rect.Width > 0).OrderBy(rect => rect.X).ToArray();
                double remaining = bounds.Sum(rect => rect.Width) * Math.Clamp(highlight.Progress, 0, 1);
                foreach (Rect bound in bounds)
                {
                    double fill = Math.Min(remaining, bound.Width);
                    if (fill > 0)
                    {
                        if (activeOutlinePen is not null)
                        { _highlightPainter.Draw(context, layout.PrimaryGeometry, bound, fill, active, activeOutlinePen); }
                        else
                        {
                            context.PushClip(new RectangleGeometry(new Rect(bound.X, bound.Y, fill, bound.Height)));
                            context.DrawGeometry(active, null, layout.PrimaryGeometry);
                            context.Pop();
                        }
                    }

                    remaining -= fill;
                }
            }

            // The auxiliary line follows the primary line, without an independent timing track.
            if (layout.AnnotationGeometry is not null && item.IsActive && item.Highlights.Count > 0)
            {
                if (activeOutlinePen is not null)
                {
                    _highlightPainter.Draw(context, layout.AnnotationGeometry,
                        new Rect(0, 0, layout.AnnotationWidth, layout.PrimaryTop),
                        layout.AnnotationWidth * item.AuxiliaryProgress, active, activeOutlinePen, isolateRange: false);
                }
                else
                {
                    context.PushClip(new RectangleGeometry(new Rect(0, 0,
                        layout.AnnotationWidth * item.AuxiliaryProgress, layout.PrimaryTop)));
                    context.DrawGeometry(active, null, layout.AnnotationGeometry);
                    context.Pop();
                }
            }

            context.Pop();
        }

        context.Pop();
    }

    private TextLayout GetLayout(LayoutKey key)
    {
        if (_layouts.TryGetValue(key, out TextLayout? cached))
        {
            return cached;
        }

        var typeface = new Typeface(new FontFamily(SystemFontCatalog.Resolve(key.FontFamily)), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        FormattedText primary = Format(key.Primary, typeface, key.PrimarySize);
        FormattedText? annotation = key.Annotation.Length > 0 ? Format(key.Annotation, typeface, key.AnnotationSize) : null;
        double gap = annotation is null ? 0 : key.LineGap;
        double naturalWidth = Math.Max(primary.WidthIncludingTrailingWhitespace, annotation?.WidthIncludingTrailingWhitespace ?? 0);
        double naturalHeight = primary.Height + (annotation?.Height ?? 0) + gap;
        double fit = Math.Min(1, Math.Min(key.MaxWidth / Math.Max(1, naturalWidth), key.MaxHeight / Math.Max(1, naturalHeight)));
        if (fit < 1)
        {
            primary = Format(key.Primary, typeface, Math.Max(1, key.PrimarySize * fit));
            annotation = annotation is null ? null : Format(key.Annotation, typeface, Math.Max(1, key.AnnotationSize * fit));
            gap *= fit;
        }

        double primaryTop = (annotation?.Height ?? 0) + gap;
        Geometry geometry = primary.BuildGeometry(new Point(0, primaryTop));
        geometry.Freeze();
        Geometry? annotationGeometry = annotation?.BuildGeometry(new Point(0, 0));
        annotationGeometry?.Freeze();
        var layout = new TextLayout(primary, geometry, annotationGeometry, primaryTop,
            Math.Max(primary.WidthIncludingTrailingWhitespace, annotation?.WidthIncludingTrailingWhitespace ?? 0),
            primaryTop + primary.Height, annotation?.WidthIncludingTrailingWhitespace ?? 0);
        if (_layouts.Count >= 256)
        {
            _layouts.Clear();
        }

        _layouts[key] = layout;
        return layout;
    }

    private static FormattedText Format(string text, Typeface typeface, double fontSize) => new(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface,
        fontSize, Brushes.White, pixelsPerDip: 1);

    private static void DrawText(DrawingContext context, Geometry geometry, Brush fill, Pen pen, double shadow)
    {
        if (shadow > 0)
        {
            context.PushTransform(new TranslateTransform(shadow, shadow));
            context.DrawGeometry(Brushes.Black, pen, geometry);
            context.Pop();
        }

        // Draw the outline first, then restore the complete glyph fill. Stroking
        // after filling would erase thin kana strokes at the smaller annotation size.
        context.DrawGeometry(pen.Brush, pen, geometry);
        context.DrawGeometry(fill, null, geometry);
    }

    private Brush ColorBrush(string value)
    {
        if (_brushes.TryGetValue(value, out Brush? cached))
        {
            return cached;
        }

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        if (_brushes.Count >= 64)
        {
            _brushes.Clear();
        }

        _brushes[value] = brush;
        return brush;
    }

    private static string Annotation(LyricLine line) => line.AnnotationType switch
    {
        AnnotationType.Kana or AnnotationType.Pinyin => line.ReadingText ?? line.AnnotationText ?? string.Empty,
        AnnotationType.Romaji => line.RomanizationText ?? string.Empty,
        AnnotationType.Translation => line.TranslationText ?? string.Empty,
        _ => string.Empty,
    };

    private static bool IsNonJapanese(SubtitleLineScene scene)
    {
        // Source spans, when available, take precedence over the document's legacy default.
        // A mixed Japanese/English line must keep its per-run ruby and romanization toggle.
        if (scene.Line.LanguageSpans is { Count: > 0 } spans)
        { return spans.All(span => span.LanguageId != LanguageIds.Japanese); }
        return scene.Language != LyricLanguage.Japanese
            && scene.Line.Units.All(unit => unit.LanguageId != LanguageIds.Japanese)
            && scene.Line.Presentation!.SubtitleRuns.All(run => run.LanguageId != LanguageIds.Japanese);
    }

    private sealed record LayoutKey(string Primary, string Annotation, string FontFamily,
        double PrimarySize, double AnnotationSize, double MaxWidth, double MaxHeight, double LineGap);

    private sealed record TextLayout(FormattedText PrimaryText, Geometry PrimaryGeometry,
        Geometry? AnnotationGeometry, double PrimaryTop, double Width, double Height, double AnnotationWidth);
}
