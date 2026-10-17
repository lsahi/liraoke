using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace KaraokeFilmMaker.App.Controls;

/// <summary>A compact relative value wheel, so large offset ranges retain useful fine adjustment.</summary>
public sealed class MetronomeValueWheel : RangeBase
{
    private const double PixelsPerStep = 8;
    private Point? _dragStart;
    private double _dragValue, _dragStep;

    public double Step { get => SmallChange; set { SmallChange = value; LargeChange = value * 5; } }
    public double FineStep { get; set; } = 0.1;

    public MetronomeValueWheel()
    {
        Height = 34;
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.SizeWE;
        ValueChanged += (_, _) => InvalidateVisual();
        IsKeyboardFocusWithinChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Adjust the draft without activating playback or changing the project.</summary>
    public void Adjust(int steps, bool fine)
    {
        SetCurrentValue(ValueProperty, Math.Clamp(Math.Round(Value + steps * (fine ? FineStep : Step), 8), Minimum, Maximum));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ValueWheelAutomationPeer(this);

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth <= 0 || ActualHeight <= 0) { return; }
        var border = new Pen(Brush(IsKeyboardFocusWithin ? "#8DC5D7" : "#DDEBF1"), 1);
        drawing.DrawRoundedRectangle(Brush("#F8FCFE"), border, new Rect(0.5, 0.5, ActualWidth - 1, ActualHeight - 1), 5, 5);
        double center = ActualWidth / 2;
        const double spacing = 9;
        double scaleStep = Step;
        for (int i = -(int)(center / spacing); i <= center / spacing; i++)
        {
            double number = Value + i * scaleStep;
            if (number < Minimum || number > Maximum) { continue; }
            bool major = i % 5 == 0;
            double x = center + i * spacing;
            drawing.DrawLine(new Pen(Brush(major ? "#98BACA" : "#D3E4EC"), 1),
                new Point(x, 6), new Point(x, major ? 15 : 11));
            if (major && x > 15 && x < ActualWidth - 15)
            {
                var label = Text(number.ToString("0.#", CultureInfo.CurrentCulture), i == 0 ? "#446878" : "#8295A0");
                drawing.DrawText(label, new Point(x - label.Width / 2, 18));
            }
        }
        drawing.DrawLine(new Pen(Brush("#D7AE4A"), 2), new Point(center, 2), new Point(center, 16));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _dragStart = e.GetPosition(this);
        _dragValue = Value;
        _dragStep = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? FineStep : Step;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStart is not { } start || !IsMouseCaptured) { return; }
        double steps = Math.Round((e.GetPosition(this).X - start.X) / PixelsPerStep, MidpointRounding.AwayFromZero);
        SetCurrentValue(ValueProperty, Math.Clamp(Math.Round(_dragValue + steps * _dragStep, 8), Minimum, Maximum));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragStart is null) { return; }
        _dragStart = null;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    { base.OnLostMouseCapture(e); _dragStart = null; }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Delta == 0) { return; }
        Adjust(Math.Sign(e.Delta) * Math.Max(1, Math.Abs(e.Delta) / 120), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int steps = e.Key switch { Key.Left or Key.Down => -1, Key.Right or Key.Up => 1, _ => 0 };
        if (steps == 0) { return; }
        Adjust(steps, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    private FormattedText Text(string text, string color) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), 9, Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private sealed class ValueWheelAutomationPeer(MetronomeValueWheel owner) : RangeBaseAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(MetronomeValueWheel);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
    }
}
