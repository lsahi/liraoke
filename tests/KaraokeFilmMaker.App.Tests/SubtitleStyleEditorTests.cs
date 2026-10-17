using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Infrastructure.Rendering;

namespace KaraokeFilmMaker.App.Tests;

public sealed class SubtitleStyleEditorTests
{
    [Fact]
    public async Task PresetSelectionIsDraftUntilAppliedAndCanUndoRedoAndRoundTrip() => await Sta(async () =>
    {
        var window = new MainWindow();
        string directory = Path.Combine(Path.GetTempPath(), "Liraoke.StyleTests", Guid.NewGuid().ToString("N"));
        try
        {
            var project = KaraokeProject.Create("Style", DateTimeOffset.UtcNow);
            var session = new WorkspaceSession(directory, project);
            Field("_workspace").SetValue(window, session);
            foreach (string method in new[] { "RefreshLyricRows", "RefreshVideoRows", "RefreshSubtitleStyleFields", "RefreshExportPreset" }) { Call(method); }
            var presets = (ComboBox)window.FindName("SubtitlePresetCombo");
            Assert.Equal(SubtitleStylePresets.All.Count + 1, presets.Items.Count);
            presets.SelectedIndex = 2;
            Assert.Equal(project.SubtitleStyle, Current().SubtitleStyle);
            Assert.Equal("#FF000000", ((ComboBox)window.FindName("ActiveOutlineColorText")).Text);
            Call("ApplySubtitleStyleFields");
            Call("RecordHistory", session);
            var applied = Current().SubtitleStyle;
            Assert.Equal("#FF000000", applied.ActiveOutlineColor);
            await (Task)Call("RestoreHistoryAsync", false)!;
            Assert.Equal(project.SubtitleStyle, Current().SubtitleStyle);
            await (Task)Call("RestoreHistoryAsync", true)!;
            Assert.Equal(applied, Current().SubtitleStyle);
            var repository = new JsonProjectRepository();
            await repository.SaveAsync(directory, Current());
            Assert.Equal(applied, (await repository.LoadAsync(directory)).SubtitleStyle);

            KaraokeProject Current() => ((WorkspaceSession)Field("_workspace").GetValue(window)!).Project;
            object? Call(string method, params object?[] args) => Method(method).Invoke(window, args);
        }
        finally
        {
            Field("_workspace").SetValue(window, null); Field("_allowClose").SetValue(window, true); window.Close();
            if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
        }
    });

    [Fact]
    public async Task MissingFontAndLegacyOutlineStayIntactAndInstalledFamiliesAreSearchable() => await Sta(() =>
    {
        var window = new MainWindow();
        try
        {
            var project = KaraokeProject.Create("Legacy font", DateTimeOffset.UtcNow);
            project = project with { SubtitleStyle = project.SubtitleStyle with
            { FontFamily = "Missing project font 70416", ActiveOutlineColor = null, SafeAreaRatio = 0.4 } };
            Field("_workspace").SetValue(window, new WorkspaceSession(Path.GetTempPath(), project));
            Method("RefreshSubtitleStyleFields").Invoke(window, null);
            var combo = (ComboBox)window.FindName("SubtitleFontText");
            Assert.True(combo.IsEditable);
            Assert.Equal(project.SubtitleStyle.FontFamily, combo.Text);
            Assert.Contains("本机未安装", ((TextBlock)window.FindName("SubtitleFontStatusText")).Text);
            Assert.Contains(SystemFontCatalog.Resolve(combo.Text), ((TextBlock)window.FindName("SubtitleFontStatusText")).Text);
            Assert.Equal("", ((ComboBox)window.FindName("ActiveOutlineColorText")).Text);
            Method("ApplySubtitleStyleFields").Invoke(window, null);
            var saved = ((WorkspaceSession)Field("_workspace").GetValue(window)!).Project;
            Assert.Equal(project.SubtitleStyle, saved.SubtitleStyle);
            var tabs = (TabControl)window.FindName("EditorTabs");
            tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(tab => (string)tab.Header == "字幕样式");
            var content = (FrameworkElement)window.Content;
            foreach (var size in new[] { new Size(1100, 760), new Size(1440, 940) })
            {
                content.Measure(size); content.Arrange(new Rect(new Point(), size)); content.UpdateLayout();
                Assert.True(combo.ActualWidth > 100);
                var apply = (Button)window.FindName("ApplySubtitleStyleButton");
                Point position = apply.TranslatePoint(new Point(), content);
                Assert.InRange(position.Y + apply.ActualHeight, 0, tabs.TranslatePoint(new Point(), content).Y + tabs.ActualHeight);
                Snapshot(content, $"subtitle-style-{size.Width}.png", size);
            }
            combo.ApplyTemplate();
            var input = (TextBox)combo.Template.FindName("PART_EditableTextBox", combo);
            input.Text = "goth";
            Assert.Equal("goth", combo.Text);
            Assert.Equal(SystemFontCatalog.Search("goth"), combo.Items.Cast<string>().ToArray());
            Assert.NotEmpty(combo.Items);
            combo.SelectedIndex = 0;
            Assert.True(SystemFontCatalog.IsInstalled(combo.Text));
            Assert.Contains("使用本机字体", ((TextBlock)window.FindName("SubtitleFontStatusText")).Text);
        }
        finally { Field("_workspace").SetValue(window, null); Field("_allowClose").SetValue(window, true); window.Close(); }
        return Task.CompletedTask;
    });

    private static void Snapshot(FrameworkElement content, string name, Size size)
    {
        string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) { return; }
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static async Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}
