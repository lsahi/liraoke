using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.App.Controls;

/// <summary>A single unwrapped lyric row. The parent scroll viewer follows the active singing unit.</summary>
public sealed class RecordingLyricLine : FrameworkElement
{
    private LyricLine? _line;
    private LyricLanguage _language;
    private bool _romaji;
    private int _active = -1, _completed;
    private Guid? _selected;
    private HashSet<Guid> _multiSelected = [];
    private bool _wholeLineSelected;
    private IReadOnlyList<SingingRole>? _appearanceRoles;
    private LyricLine? _appearanceLine;
    private readonly Dictionary<Guid, Brush[]> _roleColors = [];
    public void SetRoleAppearance(IReadOnlyList<SingingRole> roles)
    {
        if (ReferenceEquals(roles, _appearanceRoles) && ReferenceEquals(_line, _appearanceLine)) return;
        _appearanceRoles = roles; _appearanceLine = _line; _roleColors.Clear();
        if (_line is not null)
            foreach (var unit in _line.Units)
            {
                var colors = roles.Where(r => RoleAssignments.Effective(_line, unit).Contains(r.Id)).Select(r =>
                { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(r.Style.ActiveColor)); brush.Freeze(); return (Brush)brush; }).ToArray();
                if (colors.Length > 0) _roleColors[unit.Id] = colors;
            }
        InvalidateVisual();
    }
    public void SetRoleSelection(IReadOnlySet<Guid> units, bool wholeLine)
    {
        var relevant = _cells.Where(cell => units.Contains(cell.Id)).Select(cell => cell.Id).ToHashSet();
        if (_wholeLineSelected == wholeLine && _multiSelected.SetEquals(relevant)) return;
        _multiSelected = relevant; _wholeLineSelected = wholeLine; InvalidateVisual();
    }
    private IReadOnlySet<Guid>? _pendingUnits;
    private int _pendingRevision;
    private readonly List<Cell> _cells = [];
    private readonly List<(string Text, double X, double Width, bool Secondary)> _originals = [];
    private bool _hasSecondary, _hasRomanization;
    private double _width;
    private readonly Dictionary<(string Text, double Size), (Geometry Geometry, double Width)> _glyphs = [];
    private static readonly Brush ActiveBackground = FrozenBrush(233, 247, 252);
    private static readonly Brush InactiveBackground = FrozenBrush(255, 255, 255);
    private static readonly Brush Ink = FrozenBrush(52, 66, 77);
    private static readonly Brush MutedInk = FrozenBrush(104, 119, 130);
    private static readonly Brush PendingInk = FrozenBrush(145, 106, 25);
    private static readonly Brush SavedInk = FrozenBrush(57, 116, 80);
    private static readonly Brush SelectedBorder = FrozenBrush(185, 145, 46);
    private static readonly Brush ActiveBorder = FrozenBrush(105, 172, 198);
    private static readonly Brush CellBorder = FrozenBrush(200, 220, 230);
    public event Action<Guid, Guid>? UnitSelected;
    public Guid? LineId => _line?.Id;
    public int ActiveUnitIndex => _active;

    public void Update(LyricLine? line, LyricLanguage language, bool romaji, int active, int completed, Guid? selected,
        IReadOnlySet<Guid>? pendingUnits = null, int pendingRevision = 0)
    {
        bool layoutChanged = !ReferenceEquals(line, _line) || language != _language || romaji != _romaji;
        if (!layoutChanged && active == _active && completed == _completed && selected == _selected
            && ReferenceEquals(_pendingUnits, pendingUnits) && _pendingRevision == pendingRevision) { return; }
        _line = line; _language = language; _romaji = romaji; _active = active; _completed = completed; _selected = selected;
        _pendingUnits = pendingUnits; _pendingRevision = pendingRevision;
        if (layoutChanged) { Rebuild(); InvalidateMeasure(); }
        InvalidateVisual();
    }

    public void RevealActive(ScrollViewer viewer)
    {
        var cell = _cells.FirstOrDefault(cell => cell.Index == _active);
        if (cell is null || viewer.ViewportWidth <= 0) { return; }
        if (cell.X < viewer.HorizontalOffset) { viewer.ScrollToHorizontalOffset(Math.Max(0, cell.X - 12)); }
        else if (cell.X + cell.Width + 12 > viewer.HorizontalOffset + viewer.ViewportWidth)
        { viewer.ScrollToHorizontalOffset(cell.X + cell.Width + 12 - viewer.ViewportWidth); }
    }

    private void Rebuild()
    {
        _cells.Clear(); _originals.Clear(); _glyphs.Clear(); _width = 0;
        _hasSecondary = false; _hasRomanization = false;
        if (_line is null) { return; }
        // Presentation was resolved when lyrics changed. Tapping and repainting only consume it.
        var presentation = LyricPresentationLayout.HasCurrentSnapshot(_line) ? _line.Presentation : null;
        var displayCells = presentation?.RecordingCells.GroupBy(cell => cell.UnitId)
            .ToDictionary(group => group.Key, group => group.First());
        bool legacyJapanese = presentation is null && _language == LyricLanguage.Japanese;
        var groups = presentation is null ? JapaneseRubyBuilder.BuildTimingGroups(_line, false) : BuildPresentationGroups(_line);
        foreach (var group in groups)
        {
            double start = _width;
            List<string> secondaryTexts = [];
            foreach (int index in group.UnitIndexes)
            {
                var unit = _line.Units[index];
                LyricRecordingCell? display = displayCells?.GetValueOrDefault(unit.Id);
                string text = display?.PrimaryText ?? (legacyJapanese ? unit.Reading
                    : string.Concat(unit.DisplayRanges.Select(range => _line.PrimaryText.Substring(range.Start, range.Length))));
                string romaji = _romaji ? display?.Romanization ?? (legacyJapanese ? JapaneseRubyBuilder.Romaji(_line, index) : "") : "";
                if (display?.SecondaryText is { Length: > 0 } secondaryText) { secondaryTexts.Add(secondaryText); }
                _hasRomanization |= romaji.Length > 0;
                double cellWidth = Math.Max(24, Math.Max(Text(text, 19).Width, Text(romaji, 10).Width) + 10);
                _cells.Add(new(index, unit.Id, text, romaji, _width, cellWidth));
                _width += cellWidth + 2;
            }
            bool secondary = legacyJapanese || secondaryTexts.Count > 0;
            string original = secondaryTexts.Count > 0 ? string.Concat(secondaryTexts.Distinct()) : group.Text;
            if (presentation is not null && group.UnitIndexes.Any(index => _line.Units[index].DisplayRanges.Count > 1))
            { original = group.Text; } // Keep intervening source punctuation/text in a shared source group.
            double minimum = Text(original, 18).Width + 8;
            if (_width - start < minimum)
            {
                double extra = minimum - (_width - start);
                if (group.UnitIndexes.Count > 0)
                {
                    int first = _cells.Count - group.UnitIndexes.Count;
                    for (int i = first; i < _cells.Count; i++)
                    {
                        var cell = _cells[i]; double share = extra / group.UnitIndexes.Count;
                        _cells[i] = cell with { X = cell.X + (i - first) * share, Width = cell.Width + share };
                    }
                }
                _width += extra;
            }
            if (secondary || group.UnitIndexes.Count == 0)
            { _originals.Add((original, start, _width - start, secondary)); }
            _hasSecondary |= secondary;
        }
    }

    private static IReadOnlyList<RubyGroup> BuildPresentationGroups(LyricLine line)
    {
        // One singing unit must remain one tappable cell even if a plugin maps it to
        // several source ranges. Only the recording layout uses this bounding group;
        // subtitle highlighting retains the original, potentially disjoint ranges.
        if (line.Units.All(unit => unit.DisplayRanges.Count <= 1))
        { return JapaneseRubyBuilder.BuildTimingGroups(line, false); }
        var grouped = line with { Units = line.Units.Select(unit => unit.DisplayRanges.Count <= 1 ? unit : unit with
        {
            DisplayRanges = [new(unit.DisplayRanges.Min(range => range.Start),
                unit.DisplayRanges.Max(range => range.EndExclusive) - unit.DisplayRanges.Min(range => range.Start))],
        }).ToArray() };
        return JapaneseRubyBuilder.BuildTimingGroups(grouped, false);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Math.Max(1, _width),
        (_hasSecondary ? 63 : 35) + (_hasRomanization ? 14 : 0));

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        foreach (var cell in _cells)
        {
            bool active = cell.Index == _active, complete = _pendingUnits?.Contains(cell.Id) ?? cell.Index < _completed;
            bool saved = _line is not null && LyricRecordingProgress.IsCompleted(_line.Units[cell.Index]);
            Brush foreground = complete ? PendingInk : saved ? SavedInk : Ink;
            bool selected = cell.Id == _selected || _wholeLineSelected || _multiSelected.Contains(cell.Id);
            var border = new Pen(selected ? SelectedBorder : active ? ActiveBorder : CellBorder, active || selected ? 1.5 : 0.75);
            dc.DrawRoundedRectangle(active ? ActiveBackground : InactiveBackground,
                border, new Rect(cell.X, 1, cell.Width, _hasRomanization ? 43 : 29), 3, 3);
            DrawCentered(dc, cell.Text, 19, foreground, cell.X, cell.Width, 3);
            if (cell.Romaji.Length > 0) { DrawCentered(dc, cell.Romaji, 10, MutedInk, cell.X, cell.Width, 27); }
            if (_roleColors.TryGetValue(cell.Id, out var colors))
                for (int i = 0; i < colors.Length; i++)
                    dc.DrawRectangle(colors[i], null, new Rect(cell.X + 2, (_hasRomanization ? 41 : 27) + i * 3d / colors.Length,
                        Math.Max(1, cell.Width - 4), 3d / colors.Length));
        }
        foreach (var original in _originals)
        { DrawCentered(dc, original.Text, original.Secondary ? 18 : 19, Ink,
            original.X, original.Width, original.Secondary ? _hasRomanization ? 48 : 34 : 3); }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var position = e.GetPosition(this);
        var cell = _cells.FirstOrDefault(cell => position.X >= cell.X && position.X < cell.X + cell.Width);
        if (cell is not null && _line is not null) { UnitSelected?.Invoke(_line.Id, cell.Id); e.Handled = true; }
        base.OnMouseLeftButtonDown(e);
    }

    private static FormattedText Text(string value, double size, Brush? brush = null) => new(value,
        CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Yu Gothic UI"), size, brush ?? Brushes.White, 1);
    private void DrawCentered(DrawingContext dc, string value, double size, Brush brush, double x, double width, double y)
    {
        if (!_glyphs.TryGetValue((value, size), out var glyph))
        {
            var text = Text(value, size); var geometry = text.BuildGeometry(new Point()); geometry.Freeze();
            glyph = (geometry, text.Width); _glyphs[(value, size)] = glyph;
        }
        dc.PushTransform(new TranslateTransform(x + (width - glyph.Width) / 2, y));
        dc.DrawGeometry(brush, null, glyph.Geometry); dc.Pop();
    }
    private static Brush FrozenBrush(byte red, byte green, byte blue)
    { var brush = new SolidColorBrush(Color.FromRgb(red, green, blue)); brush.Freeze(); return brush; }
    private sealed record Cell(int Index, Guid Id, string Text, string Romaji, double X, double Width);
}
