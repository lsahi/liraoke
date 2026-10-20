using System.Globalization;
using System.Windows;
using System.Windows.Media;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Infrastructure.Rendering;

// One geometry per written run. Chorus colors are horizontal bands, never duplicate lyrics.
internal sealed class WpfRoleSubtitlePainter
{
    private readonly Dictionary<(LyricLine, SubtitleStyle, IReadOnlyList<SingingRole>, double, double, bool), Layout> _layouts = [];
    private readonly WpfSubtitleHighlightPainter _highlight = new();
    private readonly Dictionary<(Geometry, IReadOnlyList<SubtitleStyle>, double), (Brush Inactive, Pen Outline, Brush Active, Pen ActiveOutline)> _paints = [];

    public void Draw(DrawingContext dc, SubtitleLineScene scene, SubtitleStyle standard, IReadOnlyList<SingingRole> roles,
        double width, double height, SubtitlePlacement placement)
    {
        double scale = Math.Min(width, height) / 1080;
        double cue = WpfSubtitleEntranceCue.ReservedWidth(scene, standard, scale);
        var key = (scene.Line, standard, roles, width, height, cue > 0);
        if (!_layouts.TryGetValue(key, out var layout))
        {
            bool japanese = scene.Language == LyricLanguage.Japanese || scene.Line.Units.Any(unit => unit.LanguageId == LanguageIds.Japanese)
                || scene.Line.Presentation?.SubtitleRuns.Any(run => run.LanguageId == LanguageIds.Japanese) == true;
            string wholeAnnotation = scene.Line.AnnotationType switch
            {
                AnnotationType.Translation => scene.Line.TranslationText ?? "",
                AnnotationType.Pinyin => scene.Line.ReadingText ?? scene.Line.AnnotationText ?? "",
                AnnotationType.Kana when !japanese => scene.Line.ReadingText ?? scene.Line.AnnotationText ?? "",
                AnnotationType.Romaji when !japanese => scene.Line.RomanizationText ?? "",
                _ => "",
            };
            var withoutAnnotation = scene.Line with { AnnotationType = AnnotationType.None, AnnotationText = null };
            LyricLine source = wholeAnnotation.Length > 0
                ? withoutAnnotation with { Presentation = LanguagePresentation.BuildPlain(withoutAnnotation) }
                : LyricPresentationLayout.HasCurrentSnapshot(scene.Line) ? scene.Line
                : japanese ? scene.Line : scene.Line with { Presentation = LanguagePresentation.BuildPlain(scene.Line) };
            IReadOnlyList<RubyGroup> Groups(bool romanized) => LyricPresentationLayout.HasCurrentSnapshot(source)
                ? LyricPresentationLayout.BuildSubtitleGroups(source, romanized) : JapaneseRubyBuilder.Build(source, romanized);
            var plain = Groups(false); var romanized = Groups(true);
            List<Spec> specs = [];
            int cursor = 0;
            foreach (var group in plain)
            {
                if (group.Range.Start < cursor) continue;
                var styles = StylesFor(scene.Line, group, standard, roles);
                // Kana ruby can split a shared word at written kana, whereas romaji still spans its
                // singing group (思/い/出 versus "o mo i de" over 思い出). Never require identical ranges.
                var mapped = styles[0].ShowRomanization ? romanized.FirstOrDefault(other =>
                    other.Range.Start == group.Range.Start && other.Range.EndExclusive >= group.Range.EndExclusive) ?? group : group;
                specs.Add(new(mapped, StylesFor(scene.Line, mapped, standard, roles)));
                cursor = mapped.Range.EndExclusive;
            }
            var annotationStyles = StylesFor(scene.Line, new(new(0, scene.Line.PrimaryText.Length), "", "", []), standard, roles);
            layout = Build(specs, scale, japanese, wholeAnnotation, annotationStyles);
            double fit = Math.Min(1, Math.Min((width - 2 * placement.Margin - cue) / Math.Max(1, layout.Width),
                placement.MaxTextHeight / Math.Max(1, layout.Height)));
            if (fit < 1) layout = Build(specs, scale * Math.Max(.001, fit), japanese, wholeAnnotation, annotationStyles);
            if (_layouts.Count > 128) { _layouts.Clear(); _paints.Clear(); }
            _layouts[key] = layout;
        }
        double x = scene.Slot == SubtitleSlot.TopLeft ? placement.Margin + cue : width - placement.Margin - layout.Width;
        double y = placement.Y(scene.Slot, layout.Height, height);
        dc.PushTransform(new TranslateTransform(x, y));
        WpfSubtitleEntranceCue.Draw(dc, scene, cue, layout.PrimaryTop, layout.Height - layout.PrimaryTop,
            Brush(standard.InactiveColor), Brush(standard.ActiveColor), Pen(standard.OutlineColor, standard.OutlineWidth * scale),
            standard.ActiveOutlineColor is null ? null : Pen(standard.ActiveOutlineColor, standard.OutlineWidth * scale));
        foreach (var run in layout.Runs)
        {
            DrawBands(dc, run.PrimaryGeometry, run.Styles, layout.Scale, false);
            if (run.AnnotationGeometry is { } annotation) DrawBands(dc, annotation, run.Styles, layout.Scale, false);
        }
        if (layout.WholeAnnotation is { } whole)
        {
            DrawBands(dc, whole, layout.AnnotationStyles, layout.Scale, false);
            if (scene.IsActive && scene.Highlights.Count > 0)
                DrawBands(dc, whole, layout.AnnotationStyles, layout.Scale, true,
                    new Rect((layout.Width - layout.AnnotationWidth) / 2, 0, layout.AnnotationWidth, layout.PrimaryTop),
                    layout.AnnotationWidth * scene.AuxiliaryProgress);
        }
        var ruby = new Dictionary<Run, List<(double Start, double End)>>();
        foreach (var highlight in scene.Highlights.Where(item => item.Progress > 0))
        {
            var pieces = layout.Runs.SelectMany(run => highlight.DisplayRanges.Select(range =>
            {
                int start = Math.Max(range.Start, run.Spec.Group.Range.Start), end = Math.Min(range.EndExclusive, run.Spec.Group.Range.EndExclusive);
                return (Run: run, Bounds: end <= start ? Rect.Empty :
                    run.Text.BuildHighlightGeometry(run.Origin, start - run.Spec.Group.Range.Start, end - start)?.Bounds ?? Rect.Empty);
            })).Where(item => !item.Bounds.IsEmpty).OrderBy(item => item.Bounds.Left).ToArray();
            double remaining = pieces.Sum(item => item.Bounds.Width) * highlight.Progress;
            foreach (var piece in pieces)
            {
                double fill = Math.Min(remaining, piece.Bounds.Width); remaining -= fill;
                if (fill <= 0) continue;
                DrawBands(dc, piece.Run.PrimaryGeometry, piece.Run.Styles, layout.Scale, true, piece.Bounds, fill);
                var run = piece.Run;
                if (run.AnnotationGeometry is null || run.Bounds.Width <= 0) continue;
                if (!ruby.TryGetValue(run, out var spans)) ruby.Add(run, spans = []);
                spans.Add((Math.Clamp((piece.Bounds.Left - run.Bounds.Left) / run.Bounds.Width, 0, 1),
                    Math.Clamp((piece.Bounds.Left + fill - run.Bounds.Left) / run.Bounds.Width, 0, 1)));
            }
        }
        foreach (var pair in ruby)
        foreach (var segment in Merge(pair.Value))
        {
            var run = pair.Key;
            double left = run.AnnotationOrigin.X + run.AnnotationWidth * segment.Start;
            double fill = run.AnnotationWidth * (segment.End - segment.Start);
            DrawBands(dc, run.AnnotationGeometry!, run.Styles, layout.Scale, true,
                new Rect(left, 0, fill, layout.PrimaryTop), fill);
        }
        dc.Pop();
    }

    private static IReadOnlyList<SubtitleStyle> StylesFor(LyricLine line, RubyGroup group, SubtitleStyle standard, IReadOnlyList<SingingRole> roles)
    {
        var units = group.UnitIndexes.Where(i => i >= 0 && i < line.Units.Count).Select(i => line.Units[i]).ToArray();
        if (units.Length == 0)
        {
            var previous = line.Units.LastOrDefault(unit => unit.DisplayRanges.Any(range => range.EndExclusive <= group.Range.Start))
                ?? line.Units.FirstOrDefault();
            if (previous is not null) units = [previous];
        }
        var ids = units.Length == 0 ? line.RoleIds : units.SelectMany(unit => RoleAssignments.Effective(line, unit)).Distinct().ToArray();
        var styles = roles.Where(role => ids.Contains(role.Id)).Select(role => role.Style).ToArray();
        return styles.Length == 0 ? [standard] : styles;
    }
    private void DrawBands(DrawingContext dc, Geometry geometry, IReadOnlyList<SubtitleStyle> styles, double scale,
        bool sung, Rect? sweep = null, double fillWidth = 0)
    {
        if (geometry.Bounds.IsEmpty) return;
        if (!_paints.TryGetValue((geometry, styles, scale), out var paint))
        {
            Brush Bands(Func<SubtitleStyle, string> color)
            {
                if (styles.Count == 1) return Brush(color(styles[0]));
                // A single painted geometry avoids anti-aliased transparent seams between separately clipped halves.
                var gradient = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute,
                    StartPoint = new Point(0, geometry.Bounds.Top), EndPoint = new Point(0, geometry.Bounds.Bottom) };
                for (int i = 0; i < styles.Count; i++)
                {
                    var c = (Color)ColorConverter.ConvertFromString(color(styles[i]));
                    gradient.GradientStops.Add(new(c, (double)i / styles.Count));
                    gradient.GradientStops.Add(new(c, (double)(i + 1) / styles.Count));
                }
                gradient.Freeze(); return gradient;
            }
            Pen Outline(Brush brush) { var pen = new Pen(brush, styles[0].OutlineWidth * scale) { LineJoin = PenLineJoin.Round }; pen.Freeze(); return pen; }
            paint = (Bands(s => s.InactiveColor), Outline(Bands(s => s.OutlineColor)),
                Bands(s => s.ActiveColor), Outline(Bands(s => s.ActiveOutlineColor ?? s.OutlineColor)));
            _paints[(geometry, styles, scale)] = paint;
        }
        if (!sung)
        {
            if (styles[0].ShadowOffset > 0)
            {
                dc.PushTransform(new TranslateTransform(styles[0].ShadowOffset * scale, styles[0].ShadowOffset * scale));
                dc.DrawGeometry(Brushes.Black, paint.Outline, geometry); dc.Pop();
            }
            dc.DrawGeometry(paint.Inactive, paint.Outline, geometry);
        }
        else if (sweep is { } rect) _highlight.Draw(dc, geometry, rect, fillWidth, paint.Active, paint.ActiveOutline);
    }
    private static Layout Build(IReadOnlyList<Spec> specs, double scale, bool reserveRuby,
        string wholeAnnotation, IReadOnlyList<SubtitleStyle> annotationStyles)
    {
        var measured = specs.Select(spec =>
        {
            var style = spec.Styles[0];
            var face = new Typeface(new FontFamily(SystemFontCatalog.Resolve(style.FontFamily)), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            FormattedText Format(string text, double size) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face,
                Math.Max(1, size * scale), Brushes.White, 1);
            return (Spec: spec, Primary: Format(spec.Group.Text, style.PrimaryFontSize),
                Annotation: wholeAnnotation.Length == 0 && spec.Group.Annotation.Length > 0 ? Format(spec.Group.Annotation, style.AnnotationFontSize) : null,
                Reserve: reserveRuby ? Format("あ", style.AnnotationFontSize).Height : 0);
        }).ToArray();
        double annotationHeight = measured.Select(item => Math.Max(item.Annotation?.Height ?? 0, item.Reserve)).DefaultIfEmpty().Max();
        var whole = wholeAnnotation.Length == 0 ? null : new FormattedText(wholeAnnotation, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface(new FontFamily(SystemFontCatalog.Resolve(annotationStyles[0].FontFamily)),
                FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), Math.Max(1, annotationStyles[0].AnnotationFontSize * scale), Brushes.White, 1);
        annotationHeight = Math.Max(annotationHeight, whole?.Height ?? 0);
        double primaryTop = annotationHeight > 0 ? annotationHeight + specs.Select(s => s.Styles[0].LineGap * scale).DefaultIfEmpty().Max() : 0;
        double baseline = measured.Select(item => item.Primary.Baseline).DefaultIfEmpty().Max();
        double x = 0, height = primaryTop;
        List<Run> runs = [];
        foreach (var item in measured)
        {
            double pw = item.Primary.WidthIncludingTrailingWhitespace, aw = item.Annotation?.WidthIncludingTrailingWhitespace ?? 0;
            double cell = Math.Max(pw, aw + (aw > 0 ? 6 * scale : 0));
            var origin = new Point(x + (cell - pw) / 2, primaryTop + baseline - item.Primary.Baseline);
            var annotationOrigin = new Point(x + (cell - aw) / 2, annotationHeight - (item.Annotation?.Height ?? 0));
            var primary = item.Primary.BuildGeometry(origin); primary.Freeze();
            var annotation = item.Annotation?.BuildGeometry(annotationOrigin); annotation?.Freeze();
            runs.Add(new(item.Spec, item.Primary, origin, item.Primary.BuildHighlightGeometry(origin, 0, item.Spec.Group.Text.Length)?.Bounds ?? Rect.Empty,
                primary, annotation, annotationOrigin, aw));
            height = Math.Max(height, origin.Y + item.Primary.Height); x += cell;
        }
        double width = Math.Max(x, whole?.WidthIncludingTrailingWhitespace ?? 0);
        var wholeGeometry = whole?.BuildGeometry(new Point((width - whole.WidthIncludingTrailingWhitespace) / 2, 0));
        wholeGeometry?.Freeze();
        return new(runs, width, height, primaryTop, scale, wholeGeometry, whole?.WidthIncludingTrailingWhitespace ?? 0, annotationStyles);
    }
    private static IEnumerable<(double Start, double End)> Merge(List<(double Start, double End)> source)
    {
        var spans = source.OrderBy(span => span.Start).ToArray();
        if (spans.Length == 0) yield break;
        var current = spans[0];
        foreach (var next in spans.Skip(1))
        { if (next.Start <= current.End) current.End = Math.Max(current.End, next.End); else { yield return current; current = next; } }
        yield return current;
    }
    private static Brush Brush(string value) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); return brush; }
    private static Pen Pen(string value, double width) { var pen = new Pen(Brush(value), width) { LineJoin = PenLineJoin.Round }; pen.Freeze(); return pen; }
    private sealed record Spec(RubyGroup Group, IReadOnlyList<SubtitleStyle> Styles);
    private sealed record Run(Spec Spec, FormattedText Text, Point Origin, Rect Bounds, Geometry PrimaryGeometry,
        Geometry? AnnotationGeometry, Point AnnotationOrigin, double AnnotationWidth)
    { public IReadOnlyList<SubtitleStyle> Styles => Spec.Styles; }
    private sealed record Layout(IReadOnlyList<Run> Runs, double Width, double Height, double PrimaryTop, double Scale,
        Geometry? WholeAnnotation, double AnnotationWidth, IReadOnlyList<SubtitleStyle> AnnotationStyles);
}
