using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KaraokeFilmMaker.App.Controls;

/// <summary>Static preview of the draft grid, not a playback timer or a downbeat detector.</summary>
public sealed class MetronomeBeatPreview : FrameworkElement
{
    private double _bpm = 120;
    private int? _offset = 0;
    private int _divisions = 1;

    public void Update(double bpm, int? offset, int divisions = 1)
    { _bpm = bpm; _offset = offset; _divisions = divisions; InvalidateVisual(); }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth < 40 || ActualHeight < 25) { return; }
        double left = 8, right = ActualWidth - 8;
        var muted = new SolidColorBrush(Color.FromRgb(127, 146, 157));
        var baseline = new Pen(new SolidColorBrush(Color.FromRgb(218, 231, 236)), 1);
        drawing.DrawLine(baseline, new Point(left, 10), new Point(right, 10));
        for (int second = 0; second <= 2; second++)
        {
            double x = left + (right - left) * second / 2;
            drawing.DrawLine(baseline, new Point(x, 7), new Point(x, 15));
            var label = new FormattedText($"{second}s", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 9, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawing.DrawText(label, new Point(x - label.Width / 2, 20));
        }
        if (!double.IsFinite(_bpm) || _bpm < 20 || _bpm > 400 || _offset is not { } offset
            || _divisions is not (1 or 2 or 3 or 4 or 6 or 8)) { return; }
        double interval = 60000d / _bpm / _divisions;
        int first = (int)Math.Max(0, Math.Ceiling(-offset / interval));
        var beatPen = new Pen(new SolidColorBrush(Color.FromRgb(218, 179, 80)), 2);
        var divisionPen = new Pen(new SolidColorBrush(Color.FromRgb(132, 189, 211)), 1);
        for (int index = first; ; index++)
        {
            double beat = offset + index * interval;
            if (beat > 2000.000001) { break; }
            double x = left + (right - left) * beat / 2000;
            bool wholeBeat = index % _divisions == 0;
            drawing.DrawLine(wholeBeat ? beatPen : divisionPen,
                new Point(x, wholeBeat ? 2 : 7), new Point(x, wholeBeat ? 17 : 13));
        }
    }
}
