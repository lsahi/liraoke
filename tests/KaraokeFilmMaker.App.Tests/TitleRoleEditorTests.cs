using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Infrastructure.Rendering;

namespace KaraokeFilmMaker.App.Tests;

public sealed class TitleRoleEditorTests
{
    [Fact]
    public Task StyleTargetChangesOnlyRoleAndTitleDraftParticipatesInSave() => Sta(async (window, root) =>
    {
        var project = Project();
        Set(window, project, root);
        var targets = (ComboBox)window.FindName("SubtitleStyleTargetCombo");
        targets.SelectedIndex = 1;
        ((TextBox)window.FindName("PrimaryFontSizeText")).Text = "110";
        ((ColorField)window.FindName("ActiveColorText")).Text = "#FFEE3333";
        var title = (SongTitleEditor)window.FindName("SongTitleEditor");
        Private<CheckBox>(title, "_enabled").IsChecked = true;
        Private<TextBox>(title, "_text").Text = "Custom title";
        Private<ComboBox>(title, "_position").SelectedIndex = 1;
        Call(window, "ApplySubtitleStyleFields");
        var current = Current(window);
        Assert.Equal(project.SubtitleStyle, current.SubtitleStyle);
        Assert.Equal(110, current.Roles[0].Style.PrimaryFontSize);
        Assert.Equal("#FFEE3333", current.Roles[0].Style.ActiveColor);
        Assert.Equal(project.Roles[1].Style, current.Roles[1].Style);
        Assert.True(current.SongTitle.Enabled); Assert.Equal("Custom title", current.SongTitle.Text);
        Assert.Equal(SongTitlePosition.TopRight, current.SongTitle.Position);
        targets.SelectedIndex = 0;
        Assert.Equal("64", ((TextBox)window.FindName("PrimaryFontSizeText")).Text);
        await new JsonProjectRepository().SaveAsync(root, current);
        var saved = await new JsonProjectRepository().LoadAsync(root);
        Assert.Equal(current.SongTitle, saved.SongTitle); Assert.Equal(current.Roles, saved.Roles);
    });

    [Fact]
    public Task AppliedTitleUndoRestoresEditorRatherThanReintroducingOldDraft() => Sta(async (window, root) =>
    {
        var original = Project(); Set(window, original, root);
        Call(window, "RefreshExportPreset"); Call(window, "RefreshVideoRows");
        var before = Private<WorkspaceSession>(window, "_workspace");
        var title = (SongTitleEditor)window.FindName("SongTitleEditor");
        Private<TextBox>(title, "_text").Text = "Undo this title";
        Private<CheckBox>(title, "_enabled").IsChecked = true;
        Call(window, "ApplySubtitleStyleFields"); Call(window, "RecordHistory", before);
        await (Task)Call(window, "RestoreHistoryAsync", false)!;
        Assert.Equal(original.SongTitle, Current(window).SongTitle);
        Assert.Equal(original.SongTitle, title.Read());
        Call(window, "ApplySubtitleStyleFields");
        Assert.Equal(original.SongTitle, Current(window).SongTitle);
        await (Task)Call(window, "RestoreHistoryAsync", true)!;
        Assert.Equal("Undo this title", Current(window).SongTitle.Text);
        Assert.Equal(Current(window).SongTitle, title.Read());
    });

    [Fact]
    public Task SavingEditedRoleAfterApplyingStyleCreatesReusablePreference() => Sta((window, root) =>
    {
        var project = Project(); Set(window, project, root);
        ((ComboBox)window.FindName("SubtitleStyleTargetCombo")).SelectedIndex = 1;
        ((TextBox)window.FindName("PrimaryFontSizeText")).Text = "95";
        Call(window, "ApplySubtitleStyleFields");
        Call(window, "SaveEditedRole_Click", window, new RoutedEventArgs());
        var saved = Assert.Single(new UserPreferenceStore(root).Load().Roles);
        Assert.Equal("A", saved.Name); Assert.Equal(95, saved.Style.PrimaryFontSize);
        Set(window, KaraokeProject.Create("Next", DateTimeOffset.UnixEpoch), root);
        var presets = (ComboBox)window.FindName("SavedRolesCombo");
        Assert.Single(presets.Items.Cast<SingingRole>());
        Assert.Empty(Current(window).Roles);
        return Task.CompletedTask;
    });

    [Fact]
    public Task CtrlShiftSelectionAssignsRolesWithoutEditingOrLosingPendingTaps() => Sta(async (window, root) =>
    {
        var project = Project(); Set(window, project, root);
        var original = project.Lyrics.Lines.SelectMany(l => l.Units).Select(u => (u.Id, u.Timing)).ToArray();
        var markers = Private<List<ProjectTime>>(window, "_markers");
        markers.Add(ProjectTime.FromSeconds(.25));
        var first = project.Lyrics.Lines[0]; var second = project.Lyrics.Lines[1];
        Call(window, "SelectRoleLine", first.Id, ModifierKeys.None);
        Call(window, "SelectRoleLine", second.Id, ModifierKeys.Shift);
        Assert.False(((WrapPanel)window.FindName("RecordingSingleEditActions")).IsEnabled);
        Assert.Equal(Visibility.Collapsed, ((StackPanel)window.FindName("QuickEditPanel")).Visibility);
        var boxes = ((WrapPanel)window.FindName("RoleAssignmentChoices")).Children.OfType<CheckBox>().ToArray();
        boxes[0].IsChecked = true; boxes[1].IsChecked = true;
        Call(window, "ShowRecordingLines", 3, -1, 0); // Playback/page following cannot discard the assignment draft.
        Assert.Same(boxes[0], ((WrapPanel)window.FindName("RoleAssignmentChoices")).Children[0]);
        Assert.True(boxes[0].IsChecked);
        await (Task)Call(window, "AssignSelectedRolesAsync", false)!;
        Assert.All(Current(window).Lyrics.Lines.Take(2), l => Assert.Equal(2, l.RoleIds.Count));
        Assert.Single(markers);
        Assert.Equal(original, Current(window).Lyrics.Lines.SelectMany(l => l.Units).Select(u => (u.Id, u.Timing)).ToArray());
        Call(window, "SelectRoleUnit", first.Id, first.Units[0].Id, ModifierKeys.None);
        Call(window, "SelectRoleUnit", first.Id, first.Units[1].Id, ModifierKeys.Control);
        foreach (var box in ((WrapPanel)window.FindName("RoleAssignmentChoices")).Children.OfType<CheckBox>()) box.IsChecked = false;
        await (Task)Call(window, "AssignSelectedRolesAsync", false)!;
        Assert.All(Current(window).Lyrics.Lines[0].Units, u => Assert.Empty(u.RoleIds!));
        Call(window, "ApplyLyricRowsToProject"); // Source-page rows must not restore stale assignments.
        Assert.All(Current(window).Lyrics.Lines[0].Units, u => Assert.Empty(u.RoleIds!));
        await (Task)Call(window, "AssignSelectedRolesAsync", true)!;
        Assert.All(Current(window).Lyrics.Lines[0].Units, u => Assert.Null(u.RoleIds));
        Assert.Single(markers);
        Call(window, "SelectRoleUnit", first.Id, first.Units[0].Id, ModifierKeys.None);
        Assert.True(((WrapPanel)window.FindName("RecordingSingleEditActions")).IsEnabled);
    });

    [Fact]
    public Task AdaptiveRecordingAndInspectorTabsAreUsableAtBothWindowSizes() => Sta((window, root) =>
    {
        Set(window, Project(), root);
        var tabs = (TabControl)window.FindName("EditorTabs");
        tabs.SelectedItem = window.FindName("RecordingTab");
        var content = (FrameworkElement)window.Content;
        int smallCount = 0;
        foreach (var size in new[] { new Size(1100, 760), new Size(1440, 1080) })
        {
            content.Measure(size); content.Arrange(new Rect(new Point(), size)); content.UpdateLayout();
            Call(window, "RefreshTapHint", ProjectTime.Zero);
            content.UpdateLayout();
            int count = Private<int>(window, "_visibleRecordingRows");
            if (size.Width == 1100) smallCount = count;
            else Assert.True(count > smallCount);
            var viewport = (ScrollViewer)window.FindName("RecordingLyricsViewport");
            Assert.True(viewport.ActualHeight > 140);
            Snapshot(content, $"recording-roles-{size.Width}.png", size);
        }
        tabs.SelectedItem = window.FindName("PreviewTab");
        var inspector = (TabControl)window.FindName("PreviewInspectorTabs");
        foreach (string name in new[] { "曲名显示", "角色控制" })
        {
            inspector.SelectedItem = inspector.Items.Cast<TabItem>().Single(t => (string)t.Header == name);
            var size = new Size(1100, 760); content.Measure(size); content.Arrange(new Rect(new Point(), size)); content.UpdateLayout();
            Snapshot(content, $"inspector-{name}.png", size);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task PreviewOverlayMatchesExportWithRoleBandsAndTitle() => Sta(async (window, root) =>
    {
        var project = Project();
        project = project with { Lyrics = project.Lyrics with { Lines = [project.Lyrics.Lines[0] with { RoleIds = project.Roles.Select(r => r.Id).ToArray() }] },
            SongTitle = new() { Enabled = true, Text = "Liraoke duet" } };
        var overlay = new SubtitleOverlay();
        // Same shared painters, absolute song time, and pixel dimensions.
        overlay.Update(project, ProjectTime.FromSeconds(1));
        overlay.Measure(new Size(1280, 720)); overlay.Arrange(new Rect(0, 0, 1280, 720)); overlay.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32); bitmap.Render(overlay);
        byte[] preview = new byte[1280 * 720 * 4]; bitmap.CopyPixels(preview, 1280 * 4, 0);
        await using var renderer = new WpfSubtitleFrameRenderer(project);
        var frame = await renderer.RenderAsync(ProjectTime.FromSeconds(1), 1280, 720);
        // Frame renderer normalizes premultiplication; opaque fill pixels must agree.
        var export = frame.BgraPixels.ToArray();
        for (int i = 0; i < preview.Length; i += 4)
            if (preview[i + 3] == 255 && export[i + 3] == 255) Assert.Equal(preview.AsSpan(i, 4).ToArray(), export.AsSpan(i, 4).ToArray());
        Assert.Contains(preview, v => v > 0);
    });

    private static KaraokeProject Project()
    {
        var p = KaraokeProject.Create("Title and roles", DateTimeOffset.UnixEpoch);
        var lyrics = new LyricImportService().Parse(string.Join("\n", Enumerable.Repeat("Hello world", 20)), ".txt", LyricLanguage.English);
        lyrics = lyrics with { Lines = lyrics.Lines.Select((l, i) => l with
        { Timing = new(ProjectTime.FromSeconds(i * 5), ProjectTime.FromSeconds(i * 5 + 4)),
            Units = l.Units.Select((u, j) => u with { Timing = new(ProjectTime.FromSeconds(i * 5 + j), ProjectTime.FromSeconds(i * 5 + j + 1)) }).ToArray() }).ToArray() };
        return p with { Lyrics = lyrics, Roles = [new(Guid.NewGuid(), "A", p.SubtitleStyle with { ActiveColor = "#FFEE4444" }),
            new(Guid.NewGuid(), "B", p.SubtitleStyle with { ActiveColor = "#FF55AAFF" })] };
    }
    private static void Set(MainWindow window, KaraokeProject p, string root)
    {
        typeof(MainWindow).GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new WorkspaceSession(root, p));
        foreach (string name in new[] { "RefreshWorkspaceDisplay", "RefreshLyricRows", "RefreshSubtitleStyleFields", "RefreshMarkerList" }) Call(window, name);
    }
    private static KaraokeProject Current(MainWindow w) => Private<WorkspaceSession>(w, "_workspace").Project;
    private static T Private<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static object? Call(MainWindow w, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, args);
    private static Task Sta(Func<MainWindow, string, Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "Liraoke.RoleUi", Guid.NewGuid().ToString("N"));
                var window = new MainWindow(new UserPreferenceStore(root));
                Private<DispatcherTimer>(window, "_uiTimer").Stop(); Private<DispatcherTimer>(window, "_recoveryTimer").Stop();
                try { await action(window, root); done.TrySetResult(); }
                catch (Exception e) { done.TrySetException(e); }
                finally
                {
                    typeof(MainWindow).GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, null);
                    typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                    window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true);
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
    private static void Snapshot(FrameworkElement content, string name, Size size)
    {
        var root = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR"); if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(root, name)); encoder.Save(stream);
    }
}
