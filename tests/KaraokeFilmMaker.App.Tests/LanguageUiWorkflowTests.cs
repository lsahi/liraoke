using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.App.Tests;

public sealed class LanguageUiWorkflowTests
{
    [Fact]
    public async Task MixedLyricsSurviveReadonlyRowsAndQuickReadingEditWithoutLosingTiming() => await Sta(() =>
    {
        var window = new MainWindow();
        try
        {
            var service = (LanguageAnalysisService)Field("_languageAnalysis").GetValue(window)!;
            var imported = new LyricImportService().Parse("[00:01]駅まで走ってった I love you\n[00:08]我们一起唱", ".lrc", LyricLanguage.Auto);
            var analyzed = service.AnalyzeDocumentAsync(imported).GetAwaiter().GetResult();
            var document = analyzed with { Lines = analyzed.Lines.Select(line => line with
            { Units = line.Units.Select(unit => unit with { IsConfirmed = true }).ToArray() }).ToArray() };
            var project = KaraokeProject.Create("语言 UI 回归", DateTimeOffset.UtcNow) with { Lyrics = document };
            Field("_workspace").SetValue(window, new WorkspaceSession(Path.GetTempPath(), project));
            Call("RefreshLyricRows");
            ((ComboBox)window.FindName("LyricLanguageCombo")).SelectedIndex = 1; // Acquisition hint is not an editor.
            Call("ApplyLyricRowsToProject");
            Assert.Equal(LyricLanguage.Auto, Current().Language);
            Assert.Equal(document.Lines[0].Units, Current().Lines[0].Units);
            Assert.Same(document.Lines[0].Presentation, Current().Lines[0].Presentation);
            var unit = document.Lines[0].Units.First(item => item.LanguageId == "ja" && item.Reading == "は");
            ((TabControl)window.FindName("EditorTabs")).SelectedItem = window.FindName("RecordingTab");
            Call("RecordingUnitSelected", document.Lines[0].Id, unit.Id);
            Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("QuickEditPanel")).Visibility);
            ((TextBox)window.FindName("QuickEditReading")).Text = "ほ";
            ((Task)Call("ApplyQuickEditAsync")!).GetAwaiter().GetResult();
            var updated = Current().Lines[0];
            Assert.Equal(document.Lines[0].Units.Select(item => (item.Id, item.Timing, item.IsConfirmed)),
                updated.Units.Select(item => (item.Id, item.Timing, item.IsConfirmed)));
            Assert.Equal("ほ", updated.Units.Single(item => item.Id == unit.Id).Reading);
            Assert.Equal("ほ", updated.Presentation!.RecordingCells.Single(cell => cell.UnitId == unit.Id).PrimaryText);
            Assert.Contains(updated.Presentation.SubtitleRuns, run => run.Annotation == "ほし");
            Field("_browsedRecordingLineId").SetValue(window, updated.Id);
            Field("_lastTapHint").SetValue(window, null);
            Call("RefreshTapHint", KaraokeFilmMaker.Domain.Time.ProjectTime.Zero);
            Snapshot((FrameworkElement)window.Content, "mixed-language-recording.png", 1600, 1000);

            LyricDocument Current() => ((WorkspaceSession)Field("_workspace").GetValue(window)!).Project.Lyrics;
            object? Call(string name, params object?[] args) => Method(name).Invoke(window, args);
        }
        finally { Field("_workspace").SetValue(window, null); window.Close(); }
    });

    [Fact]
    public async Task MissingPluginKeepsSnapshotButDisablesContentEditing() => await Sta(() =>
    {
        var window = new MainWindow();
        try
        {
            var line = new LyricImportService().Parse("hola", ".txt", LyricLanguage.English).Lines[0];
            var unit = line.Units[0] with { LanguageId = "es" };
            line = line with { Units = [unit], LanguageSpans = [new(Guid.NewGuid(), new(0, 4), "es", LanguageDetectionSource.Manual, IsLocked: true)],
                Presentation = new("hola", [new(unit.Id, "ho-la")], [new(new(0, 4), "custom", null, [unit.Id])]) };
            var project = KaraokeProject.Create("缺包", DateTimeOffset.UtcNow) with { Lyrics = new(LyricLanguage.Auto, [line]) };
            Field("_workspace").SetValue(window, new WorkspaceSession(Path.GetTempPath(), project));
            Method("RefreshLyricRows").Invoke(window, null);
            Assert.False((bool)Method("CanEditLanguageContent").Invoke(window, [line, null])!);
            Assert.Contains("先加载语言插件", ((TextBlock)window.FindName("StatusText")).Text);
            Method("ApplyLyricRowsToProject").Invoke(window, null);
            var current = ((WorkspaceSession)Field("_workspace").GetValue(window)!).Project.Lyrics.Lines[0];
            Assert.Same(line.Presentation, current.Presentation);
            Assert.Equal("custom", current.Presentation!.SubtitleRuns[0].Annotation);
            Assert.True((bool)Method("CanEditLanguageContent").Invoke(window, [line, new TextRange(0, 4)])!);
        }
        finally { Field("_workspace").SetValue(window, null); window.Close(); }
    });

    [Fact]
    public async Task RegionDialogRendersMixedResultsAndPrefillsTheSelectedLanguage() => await Sta(() =>
    {
        var window = new LanguageRegionEditorWindow("駅まで走ってった I love you",
            [new("zh", "中文"), new("en", "English / 英语"), new("ja", "日本語 / 日语", true)],
            [new(new(0, 8), "駅まで走ってった", "ja", "自动识别", null), new(new(9, 10), "I love you", "en", "自动识别", null)], new(3, 1));
        try
        {
            Assert.Equal("ja", ((ComboBox)window.FindName("RegionLanguage")).SelectedValue);
            Snapshot((FrameworkElement)window.Content, "language-region-editor.png", 724, 556);
        }
        finally { window.Close(); }
    });

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Snapshot(FrameworkElement content, string name, int width, int height)
    {
        string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) { return; }
        Directory.CreateDirectory(directory);
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }
    private static async Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
