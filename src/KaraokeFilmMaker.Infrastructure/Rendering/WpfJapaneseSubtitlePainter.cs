using System.Globalization;
using System.Windows;
using System.Windows.Media;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Infrastructure.Rendering;

// The mapped-run layout is shared by all language snapshots, including mixed-language lines.
// The Japanese fallback is kept for projects created before language presentation snapshots existed.
internal sealed class WpfJapaneseSubtitlePainter
{
    private readonly Dictionary<(LyricLine, SubtitleStyle, double, double, bool, bool), Layout> _layouts = [];
    private readonly WpfSubtitleHighlightPainter _highlightPainter = new();

    public void Draw(DrawingContext context, SubtitleLineScene scene, SubtitleStyle style, double width, double height, SubtitlePlacement placement)
    {
        double scale = Math.Min(width, height) / 1080;
        double margin = placement.Margin;
        bool reserveRuby = IsJapaneseLine(scene);
        double cueWidth = WpfSubtitleEntranceCue.ReservedWidth(scene, style, scale);
        var key = (scene.Line, style, width, height, reserveRuby, cueWidth > 0);
        if (!_layouts.TryGetValue(key, out Layout? layout))
        {
            var groups = LyricPresentationLayout.HasCurrentSnapshot(scene.Line)
                ? LyricPresentationLayout.BuildSubtitleGroups(scene.Line, style.ShowRomanization)
                : JapaneseRubyBuilder.Build(scene.Line, style.ShowRomanization);
            layout = Build(groups, style, scale, reserveRuby);
            double fit = Math.Min(1, Math.Min(Math.Max(1, width - 2 * margin - cueWidth) / Math.Max(1, layout.Width),
                placement.MaxTextHeight / Math.Max(1, layout.Height)));
            if (fit < 1) { layout = Build(groups, style, scale * fit, reserveRuby); }
            if (_layouts.Count >= 256) { _layouts.Clear(); }
            _layouts[key] = layout;
        }
        double x = scene.Slot == SubtitleSlot.TopLeft ? margin + cueWidth : width - margin - layout.Width;
        double y = placement.Y(scene.Slot, layout.Height, height);
        Brush inactive = BrushFor(style.InactiveColor), active = BrushFor(style.ActiveColor);
        var outline = new Pen(BrushFor(style.OutlineColor), style.OutlineWidth * layout.Scale) { LineJoin = PenLineJoin.Round };
        var activeOutline = style.ActiveOutlineColor is null ? null
            : new Pen(BrushFor(style.ActiveOutlineColor), style.OutlineWidth * layout.Scale) { LineJoin = PenLineJoin.Round };
        context.PushTransform(new TranslateTransform(x, y));
        WpfSubtitleEntranceCue.Draw(context, scene, cueWidth, layout.PrimaryTop, layout.Height - layout.PrimaryTop,
            inactive, active, outline, activeOutline);
        foreach (var run in layout.Runs)
        {
            DrawGlyph(context, run.PrimaryGeometry, inactive, outline, style.ShadowOffset * layout.Scale);
            if (run.AnnotationGeometry is not null)
            { DrawGlyph(context, run.AnnotationGeometry, inactive, outline, style.ShadowOffset * layout.Scale); }
        }
        var rubyCoverage = new Dictionary<Run, List<(double Start, double End)>>();
        foreach (var highlight in scene.Highlights.Where(item => item.Progress > 0))
        {
            var pieces = layout.Runs.SelectMany(run => highlight.DisplayRanges.Select(range =>
            {
                int start = Math.Max(range.Start, run.Group.Range.Start), end = Math.Min(range.EndExclusive, run.Group.Range.EndExclusive);
                Rect bounds = end > start ? run.Primary.BuildHighlightGeometry(run.PrimaryOrigin,
                    start - run.Group.Range.Start, end - start)?.Bounds ?? Rect.Empty : Rect.Empty;
                return (Run: run, Bounds: bounds);
            })).Where(piece => !piece.Bounds.IsEmpty).OrderBy(piece => piece.Bounds.Left).ToArray();
            double remaining = pieces.Sum(piece => piece.Bounds.Width) * Math.Clamp(highlight.Progress, 0, 1);
            foreach (var piece in pieces)
            {
                double fill = Math.Min(remaining, piece.Bounds.Width);
                if (fill > 0)
                {
                    if (piece.Run.AnnotationGeometry is not null && piece.Run.PrimaryBounds.Width > 0)
                    {
                        // Derive ruby from the exact primary sweep, not from the whole timing
                        // group's unit average. A shared 思い出 mapping must not color で early.
                        Rect primary = piece.Run.PrimaryBounds;
                        double start = Math.Clamp((piece.Bounds.Left - primary.Left) / primary.Width, 0, 1);
                        double end = Math.Clamp((piece.Bounds.Left + fill - primary.Left) / primary.Width, 0, 1);
                        if (!rubyCoverage.TryGetValue(piece.Run, out var covered))
                        { covered = []; rubyCoverage.Add(piece.Run, covered); }
                        if (end > start) { covered.Add((start, end)); }
                    }
                    if (activeOutline is not null)
                    { _highlightPainter.Draw(context, piece.Run.PrimaryGeometry, piece.Bounds, fill, active, activeOutline); }
                    else
                    {
                        context.PushClip(new RectangleGeometry(new Rect(piece.Bounds.X, piece.Bounds.Y, fill, piece.Bounds.Height)));
                        context.DrawGeometry(active, null, piece.Run.PrimaryGeometry); context.Pop();
                    }
                }
                remaining -= fill;
            }
        }
        foreach (var run in layout.Runs.Where(run => run.AnnotationGeometry is not null))
        {
            if (!rubyCoverage.TryGetValue(run, out var covered)) { continue; }
            foreach (var segment in MergeCoverage(covered))
            {
                double fill = run.AnnotationWidth * segment.End;
                // Disjoint mappings stay disjoint rather than coloring an unsung middle region.
                if (segment.Start > 0)
                {
                    double left = run.AnnotationOrigin.X + run.AnnotationWidth * segment.Start;
                    double pad = (activeOutline?.Thickness ?? 0) / 2 + 1;
                    context.PushClip(new RectangleGeometry(new Rect(left, run.AnnotationGeometry!.Bounds.Top - pad,
                        Math.Max(0, run.AnnotationGeometry.Bounds.Right + pad - left), run.AnnotationGeometry.Bounds.Height + 2 * pad)));
                }
                if (activeOutline is not null)
                {
                    _highlightPainter.Draw(context, run.AnnotationGeometry!,
                        new Rect(run.AnnotationOrigin.X, 0, run.AnnotationWidth, layout.PrimaryTop),
                        fill, active, activeOutline, isolateRange: false);
                }
                else
                {
                    context.PushClip(new RectangleGeometry(new Rect(run.AnnotationOrigin.X, 0, fill, layout.PrimaryTop)));
                    context.DrawGeometry(active, null, run.AnnotationGeometry!); context.Pop();
                }
                if (segment.Start > 0) { context.Pop(); }
            }
        }
        context.Pop();
    }

    private static IEnumerable<(double Start, double End)> MergeCoverage(List<(double Start, double End)> ranges)
    {
        if (ranges.Count == 0) { yield break; }
        var ordered = ranges.OrderBy(range => range.Start).ToArray();
        var current = ordered[0];
        foreach (var next in ordered.Skip(1))
        {
            if (next.Start <= current.End) { current.End = Math.Max(current.End, next.End); }
            else { yield return current; current = next; }
        }
        yield return current;
    }

    private static bool IsJapaneseLine(SubtitleLineScene scene)
    {
        // Persisted language metadata takes precedence over a mixed document's default language.
        var ids = (scene.Line.LanguageSpans?.Select(span => span.LanguageId) ?? [])
            .Concat(scene.Line.Units.Select(unit => unit.LanguageId))
            .Concat(scene.Line.Presentation?.SubtitleRuns.Select(run => run.LanguageId) ?? [])
            .Where(id => !string.IsNullOrEmpty(id) && id != LanguageIds.Unknown).ToArray();
        if (ids.Length > 0) { return ids.Contains(LanguageIds.Japanese); }
        return scene.Language == LyricLanguage.Japanese || scene.Line.PrimaryText.Any(IsKana)
            || scene.Line.Presentation?.SubtitleRuns.Any(run => run.Annotation.Any(IsKana)) == true;
    }

    private static bool IsKana(char value) => value is >= '\u3041' and <= '\u30ff' or >= '\uff66' and <= '\uff9f';

    private static Layout Build(IReadOnlyList<RubyGroup> groups, SubtitleStyle style, double scale, bool reserveRuby)
    {
        var face = new Typeface(new FontFamily(SystemFontCatalog.Resolve(style.FontFamily)), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var measured = groups.Select(group => (Group: group,
            Primary: Format(group.Text, face, style.PrimaryFontSize * scale),
            Annotation: group.Annotation.Length == 0 ? null : Format(group.Annotation, face, style.AnnotationFontSize * scale))).ToArray();
        double annotationHeight = measured.Select(run => run.Annotation?.Height ?? 0).DefaultIfEmpty().Max();
        // Keep the ruby row even when this Japanese sentence has no kanji. Otherwise height-fit
        // enlarges its primary glyphs relative to neighboring annotated sentences.
        if (reserveRuby) { annotationHeight = Math.Max(annotationHeight, Format("あ", face, style.AnnotationFontSize * scale).Height); }
        double primaryTop = annotationHeight > 0 ? annotationHeight + style.LineGap * scale : 0;
        double x = 0;
        List<Run> runs = [];
        foreach (var item in measured)
        {
            double primaryWidth = item.Primary.WidthIncludingTrailingWhitespace;
            double annotationWidth = item.Annotation?.WidthIncludingTrailingWhitespace ?? 0;
            double cellWidth = Math.Max(primaryWidth, annotationWidth + (annotationWidth > 0 ? 6 * scale : 0));
            var primaryOrigin = new Point(x + (cellWidth - primaryWidth) / 2, primaryTop);
            var annotationOrigin = new Point(x + (cellWidth - annotationWidth) / 2, annotationHeight - (item.Annotation?.Height ?? 0));
            Geometry primary = item.Primary.BuildGeometry(primaryOrigin); primary.Freeze();
            Geometry? annotation = item.Annotation?.BuildGeometry(annotationOrigin); annotation?.Freeze();
            Rect primaryBounds = item.Primary.BuildHighlightGeometry(primaryOrigin, 0, item.Group.Text.Length)?.Bounds ?? Rect.Empty;
            runs.Add(new(item.Group, item.Primary, primaryOrigin, primaryBounds, primary, annotation, annotationOrigin, annotationWidth));
            x += cellWidth;
        }
        double primaryHeight = measured.Select(run => run.Primary.Height).DefaultIfEmpty().Max();
        if (reserveRuby) { primaryHeight = Math.Max(primaryHeight, Format("あ", face, style.PrimaryFontSize * scale).Height); }
        return new(runs, x, primaryTop + primaryHeight, primaryTop, scale);
    }

    private static FormattedText Format(string text, Typeface face, double size) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, Math.Max(1, size), Brushes.White, 1);
    private static Brush BrushFor(string color)
    { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    private static void DrawGlyph(DrawingContext context, Geometry geometry, Brush fill, Pen outline, double shadow)
    {
        if (shadow > 0)
        {
            context.PushTransform(new TranslateTransform(shadow, shadow));
            context.DrawGeometry(Brushes.Black, outline, geometry); context.Pop();
        }
        context.DrawGeometry(outline.Brush, outline, geometry); context.DrawGeometry(fill, null, geometry);
    }
    private sealed record Run(RubyGroup Group, FormattedText Primary, Point PrimaryOrigin, Rect PrimaryBounds, Geometry PrimaryGeometry,
        Geometry? AnnotationGeometry, Point AnnotationOrigin, double AnnotationWidth);
    private sealed record Layout(IReadOnlyList<Run> Runs, double Width, double Height, double PrimaryTop, double Scale);
}
