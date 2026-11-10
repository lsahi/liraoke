using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Application.Preferences;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Tests;
public sealed class ReadingCorrectionInterfaceTests
{
    [Fact] public Task AnnotationRemovalPersistsAndRendersWithoutDuplicateText() => Run(async (window, root) => {
        var lyrics=new LyricImportService().Parse("風が吹き灯火(ともしび)が揺れた\n消えてゆく夢幻（ゆめまぼろし）", ".txt",LyricLanguage.Japanese);
        var analysis=(KaraokeFilmMaker.Application.Languages.LanguageAnalysisService)Get(window,"_languageAnalysis")!;
        var first=lyrics.Lines[0]; var second=lyrics.Lines[1];
        first=await ReadingCorrection.ApplyAsync(first,new(first.Id,first.PrimaryText,first.PrimaryText,
            [new("風が吹き","かぜがふき"),new("灯火","ともしび"),new("(ともしび)","ともしび",NotSung:true),new("が揺れた","がゆれた")]),analysis);
        second=await ReadingCorrection.ApplyAsync(second,new(second.Id,second.PrimaryText,second.PrimaryText,
            [new("消えてゆく","きえてゆく"),new("夢幻","むげん"),new("（",""),new("ゆめまぼろし","ゆめまぼろし",NotSung:true),new("）","")]),analysis);
        var project=KaraokeProject.Create("注音清理",DateTimeOffset.UtcNow) with { Lyrics=lyrics with { Lines=[first,second] } };
        var repository=new JsonProjectRepository(); string path=Path.Combine(root,"annotation.lrn");
        await repository.SaveAsync(path,project);
        var saved=await repository.LoadAsync(path);
        Assert.Equal("風が吹き灯火が揺れた",saved.Lyrics.Lines[0].PrimaryText);
        Assert.Equal("消えてゆく夢幻",saved.Lyrics.Lines[1].PrimaryText);
        Assert.Equal("きえてゆくゆめまぼろし",string.Concat(saved.Lyrics.Lines[1].Units.Select(x=>x.Reading)));
        Assert.All(saved.Lyrics.Lines,line=>Assert.All(line.Presentation!.RecordingCells,cell=> {
            Assert.DoesNotContain("(",cell.PrimaryText); Assert.DoesNotContain("（",cell.PrimaryText);
            Assert.DoesNotContain("(",cell.SecondaryText??""); Assert.DoesNotContain("（",cell.SecondaryText??"");
        }));
        Set(window,"_workspace",new WorkspaceSession(root,saved,path));
        Call(window,"RefreshLyricRows"); Call(window,"RefreshWorkspaceDisplay");
        ((TabControl)window.FindName("EditorTabs")).SelectedItem=window.FindName("RecordingTab");
        var content=(FrameworkElement)window.Content;var size=new Size(1440,940);
        content.Measure(size);content.Arrange(new Rect(size));content.UpdateLayout();
        string? output=Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (!string.IsNullOrWhiteSpace(output)) {
            Directory.CreateDirectory(output);
            var bitmap=new RenderTargetBitmap(1440,940,96,96,PixelFormats.Pbgra32);bitmap.Render(content);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create(Path.Combine(output,"asr-annotation-cleaned.png"));encoder.Save(file);
        }
    });
    [Fact] public Task AsrConfigurationOmniShortcutFitsWindow() => Run((window, root) => {
        var dialog = new KaraokeFilmMaker.App.Dialogs.AsrConfigurationWindow(new UserPreferenceStore(root));
        try {
            dialog.Show(); dialog.UpdateLayout();
            var content = (FrameworkElement)dialog.Content;
            Assert.True(content.ActualWidth > 400);
            var bitmap = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(dialog);
            var output = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
            if (!string.IsNullOrWhiteSpace(output)) {
                Directory.CreateDirectory(output); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, "asr-omni-config.png")); encoder.Save(file);
            }
        } finally { dialog.Close(); }
        return Task.CompletedTask;
    });
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public Task CorrectionStreamsPersistsAndCanBeUndone(bool cancel, bool allFailed) => Run(async (window, root) => {
        var lyrics = new LyricImportService().Parse("星空\n帰ってくる", ".txt", LyricLanguage.Japanese);
        Guid assetId = Guid.NewGuid();
        var project = KaraokeProject.Create("ASR 校正", DateTimeOffset.UtcNow) with {
            Lyrics = lyrics, MasterAudioAssetId = assetId,
            MediaAssets = [new(assetId, "歌曲.wav", "song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(30))] };
        Set(window, "_workspace", new WorkspaceSession(root, project, Path.Combine(root, "test.lrn")));
        var store = (UserPreferenceStore)Get(window, "_userPreferenceStore")!;
        Guid providerId = Guid.NewGuid();
        store.SaveAiProvider(providerId, "测试校正模型", true, "https://example.invalid/chat", new("test-key"), "test");
        store.SaveAsr(new(CorrectionProviderId: providerId), new("test-asr"));
        Set(window, "_readingCorrectionService", new Fake(cancel, allFailed));
        Call(window, "RefreshLyricRows"); Call(window, "RefreshWorkspaceDisplay");
        var result = await (Task<object>)Call(window, "StartReadingCorrectionAsync", false, CancellationToken.None)!;
        var current = (WorkspaceSession)Get(window, "_workspace")!;
        if (!allFailed) Assert.Equal("なさ", current.Project.Lyrics.Lines[0].ReadingText);
        Assert.Equal("failed", current.Project.Lyrics.Lines[1].AiReadingReview!.Status);
                string status = ((TextBlock)window.FindName("AsrStatusText")).Text;
        Assert.Contains(cancel ? "已取消" : allFailed ? "校正失败" : "部分校正完成", status);
        Assert.Contains("诊断日志", status);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(result);
        Assert.False(json.GetProperty("completed").GetBoolean());
        Assert.Equal(allFailed ? 0 : 1, json.GetProperty("succeeded").GetInt32());
        Assert.Equal(allFailed ? 2 : 1, json.GetProperty("failed").GetInt32());
        Assert.True(File.Exists(current.ProjectFilePath));
        var repository = new JsonProjectRepository();
        // .lrn is the canonical saved file; deserialize and validate the actual persisted data.
        var disk = await File.ReadAllTextAsync(current.ProjectFilePath!);
        Assert.Contains("aiReadingReview", disk);
        var reloaded = await repository.LoadAsync(current.ProjectFilePath!);
        if (!allFailed) Assert.Equal("なさ", reloaded.Lyrics.Lines[0].ReadingText);
        Assert.Equal("failed", reloaded.Lyrics.Lines[1].AiReadingReview!.Status);
        Assert.DoesNotContain("test-asr", disk);
        Assert.Single((Stack<KaraokeProject>)Get(window, "_undoProjects")!);
        Assert.Equal(project, ((Stack<KaraokeProject>)Get(window, "_undoProjects")!).Peek());
        Assert.Equal(Visibility.Visible, ((Border)window.FindName("AsrPanel")).Visibility);
        Assert.DoesNotContain("待确认", ((TextBlock)window.FindName("CurrentRecordingLabel")).Text);
        Assert.DoesNotContain("待确认", status);
        if (!allFailed) { Assert.Equal("applied", reloaded.Lyrics.Lines[0].AiReadingReview!.Status); Assert.Empty(reloaded.Lyrics.Lines[0].AiReadingReview!.Issues); }
        ((TabControl)window.FindName("EditorTabs")).SelectedItem = window.FindName("RecordingTab");
        var content = (FrameworkElement)window.Content;
        foreach (var size in new[] { new Size(1440, 940), new Size(1100, 760) }) {
            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
            Assert.True(((Border)window.FindName("AsrPanel")).ActualHeight > 30);
            string? output = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
            if (!string.IsNullOrWhiteSpace(output)) {
                Directory.CreateDirectory(output);
                var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"asr-{cancel}-{allFailed}-{size.Width}.png")); encoder.Save(file);
            }
        }
    });
    [Fact] public void AgentToolsHaveNoCredentialOrPathParameters()
    {
        var tools = AiToolBridge.Catalog.EnumerateArray().Where(t => t.GetProperty("name").GetString() is "open_asr_configuration" or "correct_japanese_readings").ToArray();
        Assert.Equal(2, tools.Length);
        Assert.All(tools, t => Assert.Empty(t.GetProperty("parameters").GetProperty("properties").EnumerateObject()));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public Task AgentRequiresApprovalAndReportsSavedResult(bool approve) => Run(async (window, root) => {
        var lyrics = new LyricImportService().Parse("星空\n帰ってくる", ".txt", LyricLanguage.Japanese);
        Guid assetId = Guid.NewGuid();
        var project = KaraokeProject.Create("Agent ASR", DateTimeOffset.UtcNow) with { Lyrics = lyrics,
            MasterAudioAssetId = assetId, MediaAssets = [new(assetId, "song.wav", "song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(30))] };
        Set(window, "_workspace", new WorkspaceSession(root, project, Path.Combine(root, "agent.lrn")));
        var store = (UserPreferenceStore)Get(window, "_userPreferenceStore")!; Guid id = Guid.NewGuid();
        store.SaveAiProvider(id, "test", true, "https://example.invalid/chat", new("test-key"), "test");
        store.SaveAsr(new(CorrectionProviderId: id), new("test-asr"));
        Set(window, "_readingCorrectionService", new Fake(false));
        Call(window, "RefreshLyricRows"); Call(window, "RefreshWorkspaceDisplay");
        var args = System.Text.Json.JsonDocument.Parse("{}").RootElement;
        var task = (Task<object>)Call(window, "ExecuteAiToolAsync", project.Id, "correct_japanese_readings", args, CancellationToken.None)!;
        Assert.False(task.IsCompleted);
        Assert.Same(project, ((WorkspaceSession)Get(window, "_workspace")!).Project);
        var panel = (KaraokeFilmMaker.App.Controls.AiChatPanel)window.FindName("AssistantPanel");
        var messages = (StackPanel)panel.FindName("Messages");
        var card = (StackPanel)((Border)messages.Children[^1]).Child;
        var buttons = (WrapPanel)card.Children[2];
        ((Button)buttons.Children[approve ? 0 : 1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var result = System.Text.Json.JsonSerializer.SerializeToElement(await task);
        Assert.Equal(approve, result.GetProperty("executed").GetBoolean());
        if (approve) Assert.True(result.GetProperty("saved").GetBoolean());
        else Assert.Null(((WorkspaceSession)Get(window, "_workspace")!).Project.Lyrics.Lines[0].AiReadingReview);
    });
    private sealed class Fake(bool cancel, bool allFailed = false) : IReadingCorrectionService
    {
        public async IAsyncEnumerable<ReadingUpdate> CorrectAsync(string path, double duration, LyricDocument lyrics,
            AsrPreference settings, AiCredentials credentials, AiProviderPreference mapper, AiCredentials mapperCredentials,
            IProgress<string> progress, [EnumeratorCancellation] CancellationToken token) {
            await Task.Yield(); progress.Report("测试逐句更新");
            var first = lyrics.Lines[0];
            if (allFailed) yield return new(first.Id, null, "服务请求失败（HTTP 404）：请检查完整地址和模型名。");
            else yield return new(first.Id, new(first.Id, first.PrimaryText, first.PrimaryText, [new("星空", "なさ")]), Evidence: [new(0, 10, "なさ")]);
            await Task.Yield();
            if (cancel) throw new OperationCanceledException();
            yield return new(lyrics.Lines[1].Id, null, "测试音频识别失败");
        }
    }
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static object? Get(MainWindow w, string n) => typeof(MainWindow).GetField(n, Flags)!.GetValue(w);
    private static void Set(MainWindow w, string n, object? value) => typeof(MainWindow).GetField(n, Flags)!.SetValue(w, value);
    private static object? Call(MainWindow w, string n, params object[] args) => typeof(MainWindow).GetMethod(n, Flags)!.Invoke(w, args);
    private static Task Run(Func<MainWindow, string, Task> action) {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => {
                string root = Path.Combine(Path.GetTempPath(), "ReadingUi-" + Guid.NewGuid()); Directory.CreateDirectory(root);
                MainWindow? window = null;
                try { window = new MainWindow(new UserPreferenceStore(root)); await action(window, root); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
                finally { if (window is not null) { Set(window, "_workspace", null); Set(window, "_allowClose", true); window.Close(); } Directory.Delete(root, true); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
}
