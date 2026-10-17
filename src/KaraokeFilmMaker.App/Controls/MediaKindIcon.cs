using System.Windows;
using System.Windows.Media;
using KaraokeFilmMaker.Domain.Media;

namespace KaraokeFilmMaker.App.Controls;

/// <summary>Small code-native icons stay sharp at every Windows display scale.</summary>
public sealed class MediaKindIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(MediaKind),
        typeof(MediaKindIcon), new FrameworkPropertyMetadata(MediaKind.Audio, FrameworkPropertyMetadataOptions.AffectsRender));
    public MediaKind Kind { get => (MediaKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) { return; }
        dc.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        var ink = new SolidColorBrush(Color.FromRgb(65, 111, 137));
        var pen = new Pen(ink, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(242, 249, 252)), null, new Rect(0, 0, 24, 24), 5, 5);
        if (Kind == MediaKind.Audio)
        {
            dc.DrawLine(pen, new Point(9, 17), new Point(9, 7));
            dc.DrawLine(pen, new Point(9, 7), new Point(18, 5));
            dc.DrawLine(pen, new Point(18, 5), new Point(18, 15));
            dc.DrawEllipse(ink, null, new Point(6.5, 17), 2.5, 2);
            dc.DrawEllipse(ink, null, new Point(15.5, 15), 2.5, 2);
        }
        else if (Kind == MediaKind.Image)
        {
            dc.DrawRoundedRectangle(null, pen, new Rect(4, 5, 16, 14), 2, 2);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(231, 190, 84)), null, new Point(15.5, 9), 2, 2);
            dc.DrawGeometry(null, pen, Geometry.Parse("M5,17 L10,11 L14,16 L17,13 L20,17"));
        }
        else
        {
            dc.DrawRoundedRectangle(null, pen, new Rect(3.5, 5, 17, 14), 2, 2);
            dc.DrawGeometry(ink, null, Geometry.Parse("M10,8 L16,12 L10,16 Z"));
            dc.DrawLine(pen, new Point(4, 8), new Point(7, 8));
            dc.DrawLine(pen, new Point(4, 16), new Point(7, 16));
        }
        dc.Pop();
    }
}
