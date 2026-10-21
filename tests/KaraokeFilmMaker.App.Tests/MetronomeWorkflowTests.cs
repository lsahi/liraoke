using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.App.Tests;

public sealed class MetronomeWorkflowTests
{
    [Theory]
    [InlineData(0, 1, 0.5)]
    [InlineData(1, 2, 0.75)]
    [InlineData(2, 3, 0.666667)]
    [InlineData(3, 4, 0.625)]
    [InlineData(4, 6, 0.666667)]
    [InlineData(5, 8, 0.625)]
    public Task SubdivisionDraftAppliesOnlyToNewInputAndCanBeUndone(int choice, int divisions, double snapped) => Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(0.14))!);
        fixture.SetFields(true, 120, 0);
        var combo = (ComboBox)fixture.Window.FindName("MetronomeSubdivisionCombo");
        combo.SelectedIndex = choice;
        Assert.Equal(1, fixture.Project.Metronome!.SnapDivisions); // Draft only.
        await fixture.Task("ApplyMetronomeSettingsAsync");
        Assert.Equal(divisions, fixture.Project.Metronome!.SnapDivisions);
        Assert.Equal(ProjectTime.FromSeconds(0.14), Assert.Single(fixture.Markers));
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(0.64))!);
        Assert.Equal(ProjectTime.FromSeconds(snapped), fixture.Markers[1]);
        await fixture.Task("RestoreHistoryAsync", false);
        Assert.Equal(1, fixture.Project.Metronome!.SnapDivisions);
        Assert.False(fixture.Project.Metronome.Enabled);
        Assert.Equal(0, combo.SelectedIndex);
        Assert.Equal(ProjectTime.FromSeconds(snapped), fixture.Markers[1]);
        await fixture.Task("RestoreHistoryAsync", true);
        Assert.Equal(divisions, fixture.Project.Metronome!.SnapDivisions);
        Assert.Equal(choice, combo.SelectedIndex);
        Assert.Equal(ProjectTime.FromSeconds(snapped), fixture.Markers[1]);
    });

    [Fact]
    public Task LyricsUseOneDecimalOnlyForDisplayAndNeverRoundStoredTiming() => Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        var line = fixture.Project.Lyrics.Lines[0];
        line = LyricTimingService.Retiming(line, new(ProjectTime.FromSeconds(1.25), ProjectTime.FromSeconds(1.35)));
        fixture.Project = fixture.Project with { Lyrics = fixture.Project.Lyrics with { Lines = [line] } };
        fixture.Call("RefreshLyricRows");
        var row = ((DataGrid)fixture.Window.FindName("LyricsGrid")).Items[0];
        Assert.Equal(1.3.ToString("0.0", CultureInfo.CurrentCulture), row.GetType().GetProperty("StartDisplay")!.GetValue(row));
        Assert.Equal(1.4.ToString("0.0", CultureInfo.CurrentCulture), row.GetType().GetProperty("EndDisplay")!.GetValue(row));
        fixture.Call("ApplyLyricRowsToProject");
        Assert.Equal(line.Timing, fixture.Project.Lyrics.Lines[0].Timing);
        Assert.Equal(line.Units.Select(unit => unit.Timing), fixture.Project.Lyrics.Lines[0].Units.Select(unit => unit.Timing));
    });

    [Fact]
    public Task AppliedSettingsSnapAfterInputCompensationAndRetainRawTaps() => Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        fixture.SetFields(true, 120, 100);
        await fixture.Task("ApplyMetronomeSettingsAsync");
        Assert.Equal(new MetronomeSettings(true, 120, 100), fixture.Project.Metronome);
        Assert.Equal(fixture.Project.Metronome, fixture.Playback.Settings);
        ((TextBox)fixture.Window.FindName("InputOffsetText")).Text = "50";
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(0.31))!);
        Assert.Equal(ProjectTime.FromSeconds(0.6), Assert.Single(fixture.Markers));
        Assert.Equal(ProjectTime.FromSeconds(0.31), Assert.Single(fixture.Raw));
        Assert.False((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(0.4))!);
        Assert.Single(fixture.Markers);
        Assert.Contains("拍点", ((TextBlock)fixture.Window.FindName("StatusText")).Text);
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(0.92))!);
        Assert.Equal(ProjectTime.FromSeconds(1.1), fixture.Markers[1]);
        Assert.Equal(ProjectTime.FromSeconds(0.92), fixture.Raw[1]);

        fixture.SetFields(false, 100, 0);
        await fixture.Task("ApplyMetronomeSettingsAsync");
        Assert.False(fixture.Project.Metronome!.Enabled);
        Assert.Equal(new[] { ProjectTime.FromSeconds(0.6), ProjectTime.FromSeconds(1.1) }, fixture.Markers);
        fixture.Call("UndoLastMarker"); fixture.Call("UndoLastMarker");
        Assert.Empty(fixture.Markers);
        fixture.SetFields(true, 200, 0);
        await fixture.Task("ApplyMetronomeSettingsAsync");
        Assert.True(fixture.Project.Metronome!.Enabled);
        fixture.Call("RedoMarkerButton_Click", fixture.Window, new RoutedEventArgs());
        fixture.Call("RedoMarkerButton_Click", fixture.Window, new RoutedEventArgs());
        Assert.Equal(new[] { ProjectTime.FromSeconds(0.6), ProjectTime.FromSeconds(1.1) }, fixture.Markers);
        Assert.Equal(new[] { ProjectTime.FromSeconds(0.31), ProjectTime.FromSeconds(0.92) }, fixture.Raw);
        fixture.Call("ResetMarkers");
        fixture.SetFields(false, 120, 100);
        await fixture.Task("ApplyMetronomeSettingsAsync");
        Assert.False(fixture.Project.Metronome!.Enabled);
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(0.31))!);
        Assert.Equal(ProjectTime.FromSeconds(0.36), Assert.Single(fixture.Markers));
    });

    [Fact]
    public Task SettingsAreUndoableAndInvalidInputDoesNotChangeTheProject() => Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        fixture.SetFields(true, 137.5, -125);
        await fixture.Task("ApplyMetronomeSettingsAsync");
        var applied = fixture.Project.Metronome;
        Assert.Equal(new MetronomeSettings(true, 137.5, -125), applied);
        fixture.SetFields(true, double.NaN, 0);
        await fixture.Task("ApplyMetronomeSettingsAsync");
        Assert.Equal(applied, fixture.Project.Metronome);
        fixture.Call("RefreshMetronomeFields");
        await fixture.Task("RestoreHistoryAsync", false);
        Assert.False(fixture.Project.Metronome!.Enabled);
        Assert.False(fixture.Playback.Settings.Enabled);
        await fixture.Task("RestoreHistoryAsync", true);
        Assert.Equal(applied, fixture.Project.Metronome);
        Assert.Equal(applied, fixture.Playback.Settings);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task UndoAndRedoTempoPreservePendingTapsAndOnlyAffectNewInput(bool undoTap) => Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        fixture.SetFields(true, 120, 100);
        await fixture.Task("ApplyMetronomeSettingsAsync");
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(0.36))!);
        if (undoTap) { fixture.Call("UndoLastMarker"); }
        var pending = fixture.Markers.ToArray();
        var raw = fixture.Raw.ToArray();
        await fixture.Task("RestoreHistoryAsync", false);
        Assert.False(fixture.Project.Metronome!.Enabled);
        Assert.False(fixture.Playback.Settings.Enabled);
        Assert.Equal(pending, fixture.Markers);
        Assert.Equal(raw, fixture.Raw);
        await fixture.Task("RestoreHistoryAsync", true);
        Assert.True(fixture.Playback.Settings.Enabled);
        Assert.Equal(pending, fixture.Markers);
        Assert.Equal(raw, fixture.Raw);
        if (undoTap) { fixture.Call("RedoMarkerButton_Click", fixture.Window, new RoutedEventArgs()); }
        Assert.Equal(ProjectTime.FromSeconds(0.6), Assert.Single(fixture.Markers));
        Assert.Equal(ProjectTime.FromSeconds(0.36), Assert.Single(fixture.Raw));
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(0.92))!);
        Assert.Equal(ProjectTime.FromSeconds(1.1), fixture.Markers[1]);
    });

    [Fact]
    public Task OneRecordingCanMixFreeAndDifferentBeatGridsWithoutRetimingPreviousTaps() => Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        fixture.Project = fixture.Project with { Lyrics = new LyricImportService().Parse("春风吹又生", ".txt", LyricLanguage.Chinese) };
        fixture.Call("RefreshLyricRows");
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(0.31))!); // Free timing.
        fixture.SetFields(true, 120, 100);
        await fixture.Task("ApplyMetronomeSettingsAsync");
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(0.86))!); // 1.1s.
        fixture.SetFields(true, 100, 0);
        await fixture.Task("ApplyMetronomeSettingsAsync");
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(1.41))!); // 1.2s on the new grid.
        fixture.SetFields(false, 100, 0);
        await fixture.Task("ApplyMetronomeSettingsAsync");
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(1.63))!);
        fixture.Call("UndoLastMarker");
        fixture.SetFields(true, 200, 50);
        await fixture.Task("ApplyMetronomeSettingsAsync");
        fixture.Call("RedoMarkerButton_Click", fixture.Window, new RoutedEventArgs()); // Restores 1.63, not a new snap.
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(2.4))!);
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(3.1))!); // Final endpoint.
        double[] expected = [0.31, 1.1, 1.2, 1.63, 2.45, 3.05];
        Assert.Equal(expected.Select(ProjectTime.FromSeconds), fixture.Markers);
        Assert.Equal(new[] { 0.31, 0.86, 1.41, 1.63, 2.4, 3.1 }.Select(ProjectTime.FromSeconds), fixture.Raw);
        fixture.Call("ApplyUnitMarkersButton_Click", fixture.Window, new RoutedEventArgs());
        var applied = Assert.Single(fixture.Project.Lyrics.Lines);
        Assert.All(applied.Units, unit => Assert.True(unit.IsConfirmed));
        Assert.Equal(expected.Take(5).Select(ProjectTime.FromSeconds), applied.Units.Select(unit => unit.Timing.Start));
        Assert.Equal(ProjectTime.FromSeconds(3.05), applied.Timing.End);
        fixture.SetFields(false, 80, -500);
        await fixture.Task("ApplyMetronomeSettingsAsync");
        Assert.Equal(applied, fixture.Project.Lyrics.Lines[0]);
    });

    [Fact]
    public Task SwitchingGridCannotInsertNewTapsBeforeExistingTapsOrRewriteThem() => Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(0.61))!);
        fixture.SetFields(true, 120, 0);
        await fixture.Task("ApplyMetronomeSettingsAsync");
        Assert.False((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(0.62))!); // Would snap back to 0.5.
        Assert.Equal(ProjectTime.FromSeconds(0.61), Assert.Single(fixture.Markers));
        fixture.SetFields(false, 120, 0);
        await fixture.Task("ApplyMetronomeSettingsAsync");
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(0.62))!);
        Assert.Equal(new[] { ProjectTime.FromSeconds(0.61), ProjectTime.FromSeconds(0.62) }, fixture.Markers);
    });

    [Fact]
    public Task SingleFirstBeatAfterSongEndCannotCreateInvalidTaps() => Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        fixture.SetFields(true, 120, 30000);
        await fixture.Task("ApplyMetronomeSettingsAsync");
        Assert.False((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(1))!);
        Assert.Empty(fixture.Markers);
        Assert.Contains("没有有效拍点", ((TextBlock)fixture.Window.FindName("StatusText")).Text);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SongEndBetweenBeatsCanFinishTheFinalUnit(bool finishWithSpace) => Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        fixture.Playback.Duration = ProjectDuration.FromSeconds(1.4);
        fixture.SetFields(true, 120, 0);
        await fixture.Task("ApplyMetronomeSettingsAsync");
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(0.49))!);
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(1.01))!);
        await fixture.Playback.SeekAsync(ProjectTime.FromSeconds(1.4));
        if (finishWithSpace)
        {
            Assert.True((bool)fixture.Call("RecordMarker", fixture.Playback.Position)!);
            Assert.Equal(ProjectTime.FromSeconds(1.4), fixture.Markers[^1]);
        }
        fixture.Call("ApplyUnitMarkersButton_Click", fixture.Window, new RoutedEventArgs());
        var line = Assert.Single(fixture.Project.Lyrics.Lines);
        Assert.Equal(ProjectTime.FromSeconds(1.4), line.Timing.End);
        Assert.Equal(ProjectTime.FromSeconds(1), line.Units[^1].Timing.Start);
        Assert.Equal(ProjectTime.FromSeconds(1.4), line.Units[^1].Timing.End);
        Assert.Equal(ProjectTime.FromSeconds(1.01), line.Units[^1].RawInputTime);
        Assert.All(line.Units, unit => Assert.True(unit.IsConfirmed));
        Assert.Empty(fixture.Markers);
    });

    [Fact]
    public Task LineModeRejectsDuplicateBeatBeforeAdvancing() => Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        ((ComboBox)fixture.Window.FindName("TapModeCombo")).SelectedIndex = 0;
        fixture.SetFields(true, 120, 0);
        await fixture.Task("ApplyMetronomeSettingsAsync");
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(0.49))!);
        Assert.False((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(0.55))!);
        Assert.Equal(ProjectTime.FromSeconds(0.5), Assert.Single(fixture.Markers));
        Assert.Single(fixture.Raw);
    });

    [Fact]
    public Task HiddenMetronomeDoesNotClickOrSnapButPreservesSavedSettings() => Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        var saved = new MetronomeSettings(true, 120, 100);
        fixture.Project = fixture.Project with { Metronome = saved };
        ((Border)fixture.Window.FindName("MetronomeSection")).Visibility = Visibility.Collapsed;
        await fixture.Task("SynchronizeMetronomeAsync");
        Assert.False(fixture.Playback.Settings.Enabled);
        Assert.Equal(saved, fixture.Project.Metronome);
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(.333))!);
        Assert.Equal(ProjectTime.FromSeconds(.333), Assert.Single(fixture.Markers));
        Assert.Equal(saved, fixture.Project.Metronome);
    });

    private sealed class Fixture : IDisposable
    {
        public MainWindow Window { get; } = new();
        public Playback Playback { get; } = new();
        public KaraokeProject Project
        {
            get => ((WorkspaceSession)Field("_workspace").GetValue(Window)!).Project;
            set => Field("_workspace").SetValue(Window, new WorkspaceSession(Path.GetTempPath(), value));
        }
        public List<ProjectTime> Markers => (List<ProjectTime>)Field("_markers").GetValue(Window)!;
        public List<ProjectTime> Raw => (List<ProjectTime>)Field("_rawMarkers").GetValue(Window)!;
        public static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            // Retained experimental workflow, deliberately revealed only in this test fixture.
            ((Border)fixture.Window.FindName("MetronomeSection")).Visibility = Visibility.Visible;
            ((DispatcherTimer)Field("_uiTimer").GetValue(fixture.Window)!).Stop();
            ((DispatcherTimer)Field("_recoveryTimer").GetValue(fixture.Window)!).Stop();
            await ((IAudioPlaybackSession)Field("_playback").GetValue(fixture.Window)!).DisposeAsync();
            Field("_playback").SetValue(fixture.Window, fixture.Playback);
            fixture.Project = KaraokeProject.Create("节拍器", DateTimeOffset.UtcNow) with
            { Lyrics = new LyricImportService().Parse("春风", ".txt", LyricLanguage.Chinese) };
            foreach (string method in new[] { "RefreshLyricRows", "RefreshVideoRows", "RefreshSubtitleStyleFields", "RefreshExportPreset" })
            { fixture.Call(method); }
            return fixture;
        }
        public void SetFields(bool enabled, double bpm, int offset)
        {
            ((CheckBox)Window.FindName("MetronomeEnabledCheck")).IsChecked = enabled;
            ((TextBox)Window.FindName("MetronomeBpmText")).Text = bpm.ToString(CultureInfo.CurrentCulture);
            ((TextBox)Window.FindName("MetronomeOffsetText")).Text = offset.ToString(CultureInfo.CurrentCulture);
        }
        public object? Call(string name, params object?[] args) => typeof(MainWindow)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, args);
        public Task Task(string name, params object?[] args) => (Task)Call(name, args)!;
        public void Dispose() { Field("_allowClose").SetValue(Window, true); Window.Close(); }
    }

    public sealed class Playback : IAudioPlaybackSession, IMetronomePlaybackSession
    {
        public MetronomeSettings Settings { get; private set; } = new();
        public ProjectTime Position { get; private set; }
        public bool IsPlaying { get; private set; }
        public bool IsLoaded => true;
        public ProjectDuration Duration { get; set; } = ProjectDuration.FromSeconds(20);
        public string? SourcePath => null;
        public Task ConfigureMetronomeAsync(MetronomeSettings settings, CancellationToken cancellationToken = default)
        { Settings = settings; return Task.CompletedTask; }
        public Task LoadAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PlayAsync(CancellationToken cancellationToken = default) { IsPlaying = true; return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken = default) { IsPlaying = false; return Task.CompletedTask; }
        public Task SeekAsync(ProjectTime position, CancellationToken cancellationToken = default) { Position = position; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static async Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
