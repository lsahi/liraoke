using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.App.Tests;

public sealed class LanguageRecordingPresentationTests
{
    [Fact]
    public Task PartiallyOverlappingKanaSourceLabelsDoNotDuplicateKanji() => RunSta(() =>
    {
        var first = Unit(new(0, 1), "か");
        var second = Unit(new(0, 2), "えっ");
        var third = Unit(new(2, 1), "て");
        var line = new LyricLine(Guid.NewGuid(), "帰って", AnnotationType.Kana, null, null,
            new(ProjectTime.Zero, ProjectTime.FromSeconds(4)), [first, second, third], ReviewState.Unreviewed, null);
        line = line with { Presentation = KaraokeFilmMaker.Application.Languages.LanguagePresentation.BuildJapanese(line) };
        var strip = new RecordingLyricLine();
        strip.Update(line, LyricLanguage.Auto, false, 1, 1, null);
        Render(strip);
        var originals = (IEnumerable<(string Text, double X, double Width, bool Secondary)>)Field("_originals").GetValue(strip)!;
        Assert.Equal("帰って", string.Concat(originals.Select(o => o.Text)));
        Assert.Equal(new[] { "か", "えっ", "て" }, CellValues(strip, "Text"));
        var root = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (!string.IsNullOrWhiteSpace(root))
        {
            Directory.CreateDirectory(root);
            var bitmap = new RenderTargetBitmap(600, 100, 96, 96, PixelFormats.Pbgra32); bitmap.Render(strip);
            var background = new DrawingVisual();
            using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 600, 100));
            bitmap.Render(background); bitmap.Render(strip);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(root, "recording-shared-kanji.png")); png.Save(stream);
        }
    });

    [Fact]
    public async Task MixedCellsUsePersistedLabelsAndSharedJapaneseOriginalOnlyOnce()
    {
        await RunSta(() =>
        {
            var first = Unit(new(0, 2), "いっ");
            var second = Unit(new(0, 2), "しょ");
            var english = Unit(new(3, 4), "love");
            var chinese = Unit(new(8, 1), "你");
            var line = new LyricLine(Guid.NewGuid(), "一緒 love 你", AnnotationType.None, null, null,
                new(ProjectTime.Zero, ProjectTime.FromSeconds(4)), [first, second, english, chinese], ReviewState.Unreviewed, null,
                Presentation: new("一緒 love 你",
                    [new(first.Id, "いっ", "一緒", "is"), new(second.Id, "しょ", "一緒", "sho"),
                     new(english.Id, "love"), new(chinese.Id, "你")], []));
            var strip = new RecordingLyricLine();
            strip.Update(line, LyricLanguage.English, false, 0, 0, null);
            var withoutRomaji = Render(strip);
            Assert.Equal(new[] { "いっ", "しょ", "love", "你" }, CellValues(strip, "Text"));
            Assert.All(CellValues(strip, "Romaji"), Assert.Empty);
            var originals = (IEnumerable<(string Text, double X, double Width, bool Secondary)>)Field("_originals").GetValue(strip)!;
            Assert.Equal("一緒", Assert.Single(originals, item => item.Secondary).Text);
            Assert.Equal(63d, strip.DesiredSize.Height);

            strip.Update(line, LyricLanguage.Japanese, false, 0, 0, null);
            Assert.Equal(withoutRomaji, Render(strip));
            strip.Update(line, LyricLanguage.Chinese, true, 0, 0, null);
            Assert.False(withoutRomaji.SequenceEqual(Render(strip)));
            Assert.Equal(new[] { "is", "sho", "", "" }, CellValues(strip, "Romaji"));
            Assert.Equal(77d, strip.DesiredSize.Height);
        });
    }

    [Fact]
    public async Task UnknownPluginSnapshotStillProvidesRecordingLabelsWithoutLanguageCodeBranches()
    {
        await RunSta(() =>
        {
            var unit = Unit(new(0, 5), "written");
            var line = new LyricLine(Guid.NewGuid(), "hello", AnnotationType.None, null, null,
                new(ProjectTime.Zero, ProjectTime.FromSeconds(2)), [unit], ReviewState.Unreviewed, null,
                Presentation: new("hello", [new(unit.Id, "plugin label", "hello", "reference")], []));
            var strip = new RecordingLyricLine();
            strip.Update(line, LyricLanguage.Unknown, false, -1, 0, null);
            Render(strip);
            Assert.Equal("plugin label", Assert.Single(CellValues(strip, "Text")));
            // Text edits cannot keep using a stale plugin-provided display snapshot.
            strip.Update(line with { PrimaryText = "world" }, LyricLanguage.English, false, -1, 0, null);
            Render(strip);
            Assert.Equal("world", Assert.Single(CellValues(strip, "Text")));
        });
    }

    [Fact]
    public async Task OnePluginSingingUnitWithDisjointSourceRangesIsStillOneCell()
    {
        await RunSta(() =>
        {
            var unit = Unit(new(0, 1), "ひとつ") with { DisplayRanges = [new(0, 1), new(2, 1)] };
            var line = new LyricLine(Guid.NewGuid(), "空・海", AnnotationType.None, null, null,
                new(ProjectTime.Zero, ProjectTime.FromSeconds(2)), [unit], ReviewState.Unreviewed, null,
                Presentation: new("空・海", [new(unit.Id, "ひとつ", "空海")], []));
            var strip = new RecordingLyricLine();
            strip.Update(line, LyricLanguage.Auto, false, -1, 0, null);
            Render(strip);
            Assert.Equal("ひとつ", Assert.Single(CellValues(strip, "Text")));
            var originals = (IEnumerable<(string Text, double X, double Width, bool Secondary)>)Field("_originals").GetValue(strip)!;
            Assert.Equal("空・海", Assert.Single(originals).Text);
            Assert.Equal(63d, strip.DesiredSize.Height);
        });
    }

    private static string[] CellValues(RecordingLyricLine strip, string name) => ((IEnumerable)Field("_cells").GetValue(strip)!)
        .Cast<object>().Select(cell => (string)cell.GetType().GetProperty(name)!.GetValue(cell)!).ToArray();
    private static FieldInfo Field(string name) => typeof(RecordingLyricLine).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static LyricUnit Unit(TextRange range, string reading) => new(Guid.NewGuid(), [range], reading, null,
        new(ProjectTime.Zero, ProjectTime.FromSeconds(1)), null, false);
    private static byte[] Render(RecordingLyricLine strip)
    {
        strip.Measure(new Size(600, 100)); strip.Arrange(new Rect(0, 0, 600, 100)); strip.UpdateLayout();
        var bitmap = new RenderTargetBitmap(600, 100, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(strip);
        var pixels = new byte[600 * 100 * 4]; bitmap.CopyPixels(pixels, 600 * 4, 0); return pixels;
    }
    private static async Task RunSta(Action test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { test(); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
