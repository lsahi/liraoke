using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.App.Tests;

public sealed class LyricUnitMappingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FourTimingUnitsCanMapToThreeWrittenGroupsWithoutChangingTimes(bool confirmed) => Sta(() =>
    {
        var line = Line(confirmed);
        line = line with { Presentation = LanguagePresentation.BuildJapanese(line) };
        var window = new LyricUnitEditorWindow(line);
        try
        {
            var grid = (DataGrid)window.FindName("UnitsGrid");
            Assert.False(grid.Columns[0].IsReadOnly);
            string[] texts = ["思", "思", "い", "出"];
            string[] ranges = ["1-1", "1-1", "2-2", "3-3"];
            for (int i = 0; i < 4; i++)
            {
                object row = grid.Items[i];
                var notified = new List<string?>();
                ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => notified.Add(e.PropertyName);
                Set(row, "DisplayText", texts[i]);
                Assert.Equal(ranges[i], Get(row, "MappingText"));
                Assert.Contains("DisplayText", notified); Assert.Contains("MappingText", notified);
            }
            var edited = (LyricLine)typeof(LyricUnitEditorWindow).GetMethod("BuildEditedLine", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
            Assert.Equal(line.PrimaryText, edited.PrimaryText);
            Assert.Equal(line.Timing, edited.Timing);
            Assert.Equal(line.ReadingSource, edited.ReadingSource);
            Assert.Equal(line.ReadingText, edited.ReadingText);
            Assert.Equal(line.AnnotationText, edited.AnnotationText);
            Assert.Equal(line.RomanizationText, edited.RomanizationText);
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(line.Units[i], edited.Units[i] with { DisplayRanges = line.Units[i].DisplayRanges });
            }
            Assert.Equal(new[] { "おも", "", "で" }, edited.Presentation!.SubtitleRuns.Select(run => run.Annotation));
            Assert.Equal(2, edited.Presentation.SubtitleRuns[0].UnitIds.Count);
            Assert.Single(edited.Presentation.SubtitleRuns[2].UnitIds);
            SaveEditor(window);
            Assert.Null(window.EditedLine); // Draft building is not applying; cancel still leaves input untouched.
            Assert.All(line.Units, unit => Assert.Equal(new TextRange(0, 3), Assert.Single(unit.DisplayRanges)));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task NumericMappingUpdatesDisplayAndInvalidTextLeavesDraftIntact() => Sta(() =>
    {
        var window = new LyricUnitEditorWindow(Line(false));
        try
        {
            var row = ((DataGrid)window.FindName("UnitsGrid")).Items[0];
            Set(row, "MappingText", "3-3");
            Assert.Equal("出", Get(row, "DisplayText"));
            Assert.IsType<ArgumentException>(Assert.Throws<TargetInvocationException>(() => Set(row, "DisplayText", "別")).InnerException);
            Assert.Equal("3-3", Get(row, "MappingText"));
        }
        finally { window.Close(); }
    });

    private static LyricLine Line(bool confirmed) => new(Guid.NewGuid(), "思い出", AnnotationType.Kana, "おもいで", null,
        new(ProjectTime.Zero, ProjectTime.FromSeconds(5)),
        new[] { "お", "も", "い", "で" }.Select((reading, i) => new LyricUnit(Guid.NewGuid(), [new(0, 3)], reading,
            new[] { "o", "mo", "i", "de" }[i], new(new ProjectTime(i * 1000000 + 123), new ProjectTime((i + 1) * 1000000 + 123)),
            new ProjectTime(i * 1000000 + 125), confirmed, LanguageIds.Japanese)).ToArray(), ReviewState.Confirmed, ReadingSource.Confirmed);

    private static object? Get(object row, string name) => row.GetType().GetProperty(name)!.GetValue(row);
    private static void Set(object row, string name, object value) => row.GetType().GetProperty(name)!.SetValue(row, value);
    private static void SaveEditor(LyricUnitEditorWindow window)
    {
        string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (directory is null) { return; }
        Directory.CreateDirectory(directory);
        var content = (FrameworkElement)window.Content;
        var size = new Size(928, 496);
        content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(928, 496, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) { drawing.DrawRectangle(Brushes.White, null, new Rect(size)); }
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, "unit-text-mapping.png")); encoder.Save(output);
    }
    private static async Task Sta(Action action)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); result.SetResult(); } catch (Exception e) { result.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
