using System.Globalization;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Application.Timing;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.App.Tests;

public sealed class MetronomeAutoInitializationTests
{
    [Fact]
    public Task ReliableEstimateIsOnlyADraftAndUsesTheOriginalMasterMedia() => Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        var original = fixture.Project;
        fixture.Set("_playbackRate", 0.75);
        await fixture.Run();
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "media", "original-song.mp4")), fixture.Analysis.SourcePath);
        Assert.Equal(original, fixture.Project);
        Assert.False(fixture.Playback.Settings.Enabled);
        Assert.Equal(137.5, double.Parse(fixture.Text("MetronomeBpmText").Text, CultureInfo.CurrentCulture));
        Assert.Equal("125", fixture.Text("MetronomeOffsetText").Text);
        Assert.False(((CheckBox)fixture.Window.FindName("MetronomeEnabledCheck")).IsChecked);
        Assert.Contains("非准确率", fixture.Label("MetronomeAnalysisText"));
        Assert.Contains("草稿", fixture.Label("MetronomeDraftText"));
        await (Task)fixture.Call("ApplyMetronomeSettingsAsync")!;
        Assert.Equal(new MetronomeSettings(false, 137.5, 125), fixture.Project.Metronome);
        Assert.Equal(original.Lyrics, fixture.Project.Lyrics);
    });

    [Theory]
    [InlineData(false, 137.5, 125)]
    [InlineData(true, double.NaN, 125)]
    [InlineData(true, 401, 125)]
    [InlineData(true, 120, int.MaxValue)]
    public Task UnreliableOrInvalidEstimatePreservesBothDraftAndProject(bool reliable, double bpm, int offset) => Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        fixture.Text("MetronomeBpmText").Text = "101";
        fixture.Text("MetronomeOffsetText").Text = "-50";
        var original = fixture.Project;
        fixture.Analysis.Result = new(bpm, offset, 0.2, reliable, "节奏不够稳定。", []);
        await fixture.Run();
        Assert.Equal(original, fixture.Project);
        Assert.Equal("101", fixture.Text("MetronomeBpmText").Text);
        Assert.Equal("-50", fixture.Text("MetronomeOffsetText").Text);
        Assert.Contains("原数值保持不变", fixture.Label("MetronomeAnalysisText"));
    });

    [Fact]
    public Task CancellationRacingWithAnEstimateDoesNotPopulateDraft() => Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        var original = fixture.Project;
        fixture.Analysis.BeforeReturn = () => ((CancellationTokenSource)fixture.Get("_operationCancellation")!).Cancel();
        await fixture.Run();
        Assert.Equal(original, fixture.Project);
        Assert.Equal("120", fixture.Text("MetronomeBpmText").Text);
        Assert.Equal("0", fixture.Text("MetronomeOffsetText").Text);
        Assert.Contains("已取消", fixture.Label("MetronomeAnalysisText"));
        Assert.False((bool)fixture.Get("_isBusy")!);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PendingAndRedoTapsArePreservedDuringAnalysisAndApplyingItsResult(bool undo) => Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        Assert.True((bool)fixture.Call("RecordMarker", ProjectTime.FromSeconds(0.5))!);
        if (undo) { fixture.Call("UndoLastMarker"); }
        var original = fixture.Project;
        await fixture.Run();
        Assert.Equal(1, fixture.Analysis.Calls);
        Assert.Equal(original, fixture.Project);
        ((CheckBox)fixture.Window.FindName("MetronomeEnabledCheck")).IsChecked = true;
        await (Task)fixture.Call("ApplyMetronomeSettingsAsync")!;
        Assert.Equal(new MetronomeSettings(true, 137.5, 125), fixture.Project.Metronome);
        if (undo) { fixture.Call("RedoMarkerButton_Click", fixture.Window, new System.Windows.RoutedEventArgs()); }
        Assert.Equal(ProjectTime.FromSeconds(0.5), Assert.Single((List<ProjectTime>)fixture.Get("_markers")!));
        Assert.Equal(ProjectTime.FromSeconds(0.5), Assert.Single((List<ProjectTime>)fixture.Get("_rawMarkers")!));
        Assert.Equal(original.Lyrics, fixture.Project.Lyrics);
    });

    [Fact]
    public Task MissingMasterDoesNotStartAnalysis() => Sta(async () =>
    {
        using var fixture = await Fixture.Create();
        fixture.Project = fixture.Project with { MasterAudioAssetId = null };
        await fixture.Run();
        Assert.Equal(0, fixture.Analysis.Calls);
        Assert.Contains("请先导入", fixture.Label("MetronomeAnalysisText"));
    });

    private sealed class Analysis : ITempoAnalysisService
    {
        public TempoAnalysisResult Result { get; set; } = new(137.5, 125, 0.9, true, "估计拍点偏移。", [68.75, 275]);
        public Action? BeforeReturn { get; set; }
        public string? SourcePath { get; private set; }
        public int Calls { get; private set; }
        public Task<TempoAnalysisResult> AnalyzeAsync(string sourcePath, CancellationToken cancellationToken = default)
        {
            Calls++; SourcePath = sourcePath;
            cancellationToken.ThrowIfCancellationRequested();
            BeforeReturn?.Invoke();
            return Task.FromResult(Result);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public MainWindow Window { get; } = new();
        public MetronomeWorkflowTests.Playback Playback { get; } = new();
        public Analysis Analysis { get; } = new();
        public KaraokeProject Project
        {
            get => ((WorkspaceSession)Get("_workspace")!).Project;
            set => Set("_workspace", new WorkspaceSession(Path.GetTempPath(), value));
        }
        public object? Get(string field) => Field(field).GetValue(Window);
        public void Set(string field, object? value) => Field(field).SetValue(Window, value);
        public TextBox Text(string name) => (TextBox)Window.FindName(name);
        public string Label(string name) => ((TextBlock)Window.FindName(name)).Text;
        public object? Call(string method, params object?[] args) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, args);
        public Task Run() => (Task)Call("AutoInitializeMetronomeAsync")!;
        public static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            ((DispatcherTimer)fixture.Get("_uiTimer")!).Stop();
            ((DispatcherTimer)fixture.Get("_recoveryTimer")!).Stop();
            await ((IAudioPlaybackSession)fixture.Get("_playback")!).DisposeAsync();
            fixture.Set("_playback", fixture.Playback);
            fixture.Set("_tempoAnalysisService", fixture.Analysis);
            var master = new MediaAsset(Guid.NewGuid(), "原始歌曲", Path.Combine("media", "original-song.mp4"), MediaKind.Video, ProjectDuration.FromSeconds(20));
            fixture.Project = KaraokeProject.Create("分析测试", DateTimeOffset.UtcNow) with
            {
                MasterAudioAssetId = master.Id, MediaAssets = [master],
                Lyrics = new LyricImportService().Parse("春风", ".txt", LyricLanguage.Chinese)
            };
            foreach (string method in new[] { "RefreshLyricRows", "RefreshVideoRows", "RefreshSubtitleStyleFields", "RefreshExportPreset" })
            { fixture.Call(method); }
            return fixture;
        }
        public void Dispose() { Set("_allowClose", true); Window.Close(); }
        private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    }

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
