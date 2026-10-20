using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.App.Dialogs;

namespace KaraokeFilmMaker.App.Tests;

public sealed class ColorPickerTests
{
    [Theory]
    [InlineData("#83cde8", "#FF83CDE8")]
    [InlineData("  80ffD54f ", "#80FFD54F")]
    [InlineData("#0024465F", "#0024465F")]
    [InlineData("FFFFFF", "#FFFFFFFF")]
    public void PastedHexNormalizesWithoutDroppingAlpha(string input, string expected)
    {
        Assert.True(HexColor.TryParse(input, out var color)); Assert.Equal(expected, color.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("#fff")]
    [InlineData("#123456789")]
    [InlineData("red")]
    [InlineData("#GGGGGG")]
    [InlineData("##123456")]
    public void InvalidHexIsRejected(string input) => Assert.False(HexColor.TryParse(input, out _));

    [Theory]
    [InlineData(0, "#80FF0000")]
    [InlineData(60, "#80FFFF00")]
    [InlineData(120, "#8000FF00")]
    [InlineData(180, "#8000FFFF")]
    [InlineData(240, "#800000FF")]
    [InlineData(300, "#80FF00FF")]
    [InlineData(360, "#80FF0000")]
    public void HueWheelCoversSpectrumAndPreservesOpacity(double hue, string expected) =>
        Assert.Equal(expected, ColorPickerDialog.FromHsv(hue, 1, 1, 128).ToString());

    [Fact]
    public Task FieldTracksPastedHexPresetsInvalidAndInheritedColors() => Sta(() =>
    {
        var field = new ColorField { Text = "#80FFD54F" };
        Assert.Equal("#80FFD54F", field.PreviewColor!.Value.ToString());
        Input(field, "_input").Text = "83cde8";
        Assert.Equal("#FF83CDE8", field.ColorValue);
        Assert.Equal("#FF83CDE8", field.PreviewColor!.Value.ToString());
        Input(field, "_input").Text = "bad";
        Assert.Null(field.PreviewColor); Assert.Throws<InvalidOperationException>(() => field.ColorValue);
        field.AllowEmpty = true; field.Text = ""; field.FallbackText = "#FF24465F";
        Assert.Null(field.ColorValue); Assert.Equal("#FF24465F", field.PreviewColor!.Value.ToString());
        field.FallbackText = "#FFC04060";
        Assert.Equal("#FFC04060", field.PreviewColor!.Value.ToString());
        field.Text = "#00404040";
        Assert.Equal((byte)0, field.PreviewColor!.Value.A);
        field.Measure(new Size(240, 36)); field.Arrange(new Rect(0, 0, 240, 36)); field.UpdateLayout();
        var shortcutGuard = typeof(MainWindow).GetMethod("IsShortcutEditingControl", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(true, shortcutGuard.Invoke(null, [Field<Button>(field, "_picker")]));
        Assert.Equal(true, shortcutGuard.Invoke(null, [Field<Button>(field, "_copy")]));
        Snapshot(field, "color-field-transparent.png", new Size(240, 36));
    });

    [Fact]
    public Task PaletteHexSlidersAndResetStayInSyncAndInvalidTextDisablesAccept() => Sta(() =>
    {
        var original = Color.FromArgb(128, 131, 205, 232);
        var dialog = new ColorPickerDialog(original);
        try
        {
            var hex = Input(dialog, "_hex");
            var accept = Field<Button>(dialog, "_accept");
            var hue = Field<Slider>(dialog, "_hue");
            var alpha = Field<Slider>(dialog, "_alpha");
            Assert.Equal(original, dialog.SelectedColor);
            hex.Text = "#80123456"; Assert.Equal("#80123456", dialog.SelectedColor.ToString());
            Assert.Equal(128, alpha.Value);
            hue.Value = 120; Assert.Equal((byte)128, dialog.SelectedColor.A);
            alpha.Value = 64; Assert.StartsWith("#40", hex.Text);
            hex.Text = "nonsense"; Assert.False(accept.IsEnabled);
            Assert.Equal((byte)64, dialog.SelectedColor.A); // Invalid edits never replace the last good color.
            hex.Text = "00ffffff"; Assert.True(accept.IsEnabled); Assert.Equal((byte)0, dialog.SelectedColor.A);
            var panel = (StackPanel)dialog.Content;
            var actions = (StackPanel)panel.Children[panel.Children.Count - 1];
            ((Button)actions.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(original, dialog.SelectedColor);
            Assert.Equal(original.ToString(), hex.Text);
            panel.Measure(new Size(440, 550)); panel.Arrange(new Rect(0, 0, 440, 550)); panel.UpdateLayout();
            Snapshot(panel, "subtitle-color-palette.png", new Size(440, 550));
            var plane = Field<Canvas>(dialog, "_plane");
            Assert.True(plane.ActualWidth > 0);
            hue.Value = 0;
            typeof(ColorPickerDialog).GetMethod("Pick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dialog, [new Point(plane.ActualWidth * .5, plane.ActualHeight * .5)]);
            Assert.Equal("#80804040", dialog.SelectedColor.ToString());
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task PaletteCancelAndAcceptAreExplicitModalResults() => Sta(() =>
    {
        var cancelled = new ColorPickerDialog(Colors.White) { ShowInTaskbar = false };
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            Input(cancelled, "_hex").Text = "#123456";
            cancelled.Close();
        }, DispatcherPriority.ApplicationIdle);
        Assert.NotEqual(true, cancelled.ShowDialog());
        var accepted = new ColorPickerDialog(Colors.White) { ShowInTaskbar = false };
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            Input(accepted, "_hex").Text = "#80ABCDEF";
            Field<Button>(accepted, "_accept").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }, DispatcherPriority.ApplicationIdle);
        Assert.True(accepted.ShowDialog());
        Assert.Equal("#80ABCDEF", accepted.SelectedColor.ToString());
    });

    private static TextBox Input(object target, string name) => Field<TextBox>(target, name);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Snapshot(FrameworkElement content, string name, Size size)
    {
        string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (directory is null) return;
        content.Measure(size); content.Arrange(new Rect(new Point(), size)); content.UpdateLayout();
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
    private static async Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception e) { completion.TrySetException(e); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
