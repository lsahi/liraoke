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
using KaraokeFilmMaker.App.Controls;

namespace KaraokeFilmMaker.App.Tests;

public sealed class SubtitleStyleEditorTests
{
    [Fact]
    public async Task CorruptPreferencesLeaveBuiltinsAvailableAndShowErrorWithoutOverwriting() => await Sta(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "Liraoke.UserStyleUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new UserPreferenceStore(directory);
        File.WriteAllText(store.FilePath, "{");
        var window = new MainWindow(store);
        try
        {
            Field("_workspace").SetValue(window, new WorkspaceSession(directory, KaraokeProject.Create("test", DateTimeOffset.UnixEpoch)));
            Method("RefreshSubtitleStyleFields").Invoke(window, null);
            Assert.Equal(SubtitleStylePresets.All.Count + 1, ((ComboBox)window.FindName("SubtitlePresetCombo")).Items.Count);
            Assert.Contains("无法读取", ((TextBlock)window.FindName("UserPreferenceStatusText")).Text);
            ((TextBox)window.FindName("UserStyleNameText")).Text = "test";
            Method("SaveUserStyle_Click").Invoke(window, [window, new RoutedEventArgs()]);
            Assert.Contains("未能保存", ((TextBlock)window.FindName("UserPreferenceStatusText")).Text);
            Assert.Equal("{", File.ReadAllText(store.FilePath));
        }
        finally
        {
            Field("_workspace").SetValue(window, null); Field("_allowClose").SetValue(window, true); window.Close();
            Directory.Delete(directory, true);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public async Task CustomStylesSaveDraftAndLoadAcrossWindowsWithoutChangingProjectUntilApply() => await Sta(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "Liraoke.UserStyleUi", Guid.NewGuid().ToString("N"));
        var store = new UserPreferenceStore(directory);
        var first = new MainWindow(store);
        MainWindow? second = null;
        try
        {
            var project = KaraokeProject.Create("First project", DateTimeOffset.UnixEpoch);
            Field("_workspace").SetValue(first, new WorkspaceSession(directory, project));
            Method("RefreshSubtitleStyleFields").Invoke(first, null);
            ((TextBox)first.FindName("PrimaryFontSizeText")).Text = "200";
            ((ColorField)first.FindName("ActiveColorText")).Text = "80abcdef";
            Method("SaveNamedUserStyle").Invoke(first, ["大字样式", false]);
            Assert.Equal(project.SubtitleStyle, ((WorkspaceSession)Field("_workspace").GetValue(first)!).Project.SubtitleStyle);
            var saved = Assert.Single(store.Load().SubtitleStyles);
            Assert.Equal(200, saved.Style.PrimaryFontSize);
            Assert.Equal("#80ABCDEF", saved.Style.ActiveColor);
            // Identical values under different names must not silently select/delete the first preset.
            store.SaveStyle("同参数第二个样式", saved.Style);

            second = new MainWindow(new UserPreferenceStore(directory));
            var next = KaraokeProject.Create("Other project", DateTimeOffset.UnixEpoch);
            Field("_workspace").SetValue(second, new WorkspaceSession(directory, next));
            Method("RefreshSubtitleStyleFields").Invoke(second, null);
            var presets = (ComboBox)second.FindName("SubtitlePresetCombo");
            presets.SelectedIndex = presets.Items.Count - 1;
            Assert.Equal(presets.Items.Count - 1, presets.SelectedIndex);
            Assert.Equal("200", ((TextBox)second.FindName("PrimaryFontSizeText")).Text);
            Assert.Equal("#80ABCDEF", ((ColorField)second.FindName("ActiveColorText")).PreviewColor!.Value.ToString());
            Assert.Equal(next.SubtitleStyle, ((WorkspaceSession)Field("_workspace").GetValue(second)!).Project.SubtitleStyle);
            Method("ApplySubtitleStyleFields").Invoke(second, null);
            Assert.Equal(saved.Style, ((WorkspaceSession)Field("_workspace").GetValue(second)!).Project.SubtitleStyle);
            store.DeleteStyle(saved.Id);
            Assert.Equal(saved.Style, ((WorkspaceSession)Field("_workspace").GetValue(second)!).Project.SubtitleStyle);
        }
        finally
        {
            foreach (var window in new[] { first, second }.OfType<MainWindow>())
            { Field("_workspace").SetValue(window, null); Field("_allowClose").SetValue(window, true); window.Close(); }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        return Task.CompletedTask;
    });

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
            Assert.Equal("#FF000000", ((ColorField)window.FindName("ActiveOutlineColorText")).Text);
            Assert.Equal("#FF000000", ((ColorField)window.FindName("ActiveOutlineColorText")).PreviewColor!.Value.ToString());
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
            Assert.Equal("", ((ColorField)window.FindName("ActiveOutlineColorText")).Text);
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
