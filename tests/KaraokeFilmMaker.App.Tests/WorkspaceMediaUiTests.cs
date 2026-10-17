using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Application.Media;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.App.Tests;

public sealed class WorkspaceMediaUiTests
{
    [Fact]
    public void StartupArgumentsAcceptOneExistingEntryAndRejectUnsupportedOrMissingFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Liraoke.CliTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string entry = Path.Combine(directory, "项目 1.LRN");
            File.WriteAllText(entry, "{}");
            Assert.Null(WorkspaceStartupArguments.Resolve([]));
            Assert.Equal(entry, WorkspaceStartupArguments.Resolve([entry]));
            Assert.Throws<ArgumentException>(() => WorkspaceStartupArguments.Resolve([entry, entry]));
            Assert.Throws<ArgumentException>(() => WorkspaceStartupArguments.Resolve(["song.mp3"]));
            Assert.Throws<FileNotFoundException>(() => WorkspaceStartupArguments.Resolve([Path.Combine(directory, "missing.lrn")]));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task UsedMediaCancelIsUnchangedAndConfirmedRemovalCanUndoWithoutChangingLyricTiming() => await Sta(async () =>
    {
        var window = new MainWindow();
        try
        {
            var video = new MediaAsset(Guid.NewGuid(), "clip.mp4", "media/video/clip.mp4", MediaKind.Video, ProjectDuration.FromSeconds(10));
            var unused = new MediaAsset(Guid.NewGuid(), "unused.mp3", "media/audio/unused.mp3", MediaKind.Audio, ProjectDuration.FromSeconds(10));
            var project = KaraokeProject.Create("移除回归", DateTimeOffset.UtcNow) with
            {
                MediaAssets = [video, unused],
                VideoClips = [new(Guid.NewGuid(), video.Id, ProjectTime.Zero, ProjectTime.Zero, ProjectDuration.FromSeconds(5), true)],
                Lyrics = new LyricImportService().Parse("[00:01]歌词\n[00:03]保留", ".lrc", LyricLanguage.Chinese),
            };
            Field("_workspace").SetValue(window, new WorkspaceSession(Path.GetTempPath(), project));
            var previews = (Dictionary<Guid, string>)Field("_previewPaths").GetValue(window)!;
            var thumbnails = (Dictionary<Guid, string>)Field("_thumbnailPaths").GetValue(window)!;
            string proxy = Path.Combine(Path.GetTempPath(), "retained-proxy.mp4");
            string thumbnail = Path.Combine(Path.GetTempPath(), "retained-thumbnail.png");
            previews[video.Id] = proxy;
            thumbnails[video.Id] = thumbnail;
            foreach (string method in new[] { "RefreshLyricRows", "RefreshVideoRows", "RefreshSubtitleStyleFields", "RefreshExportPreset", "RefreshWorkspaceDisplay" }) { Call(method); }
            int prompts = 0;
            Func<MediaRemovalImpact, bool> cancel = impact => { prompts++; Assert.Equal(1, impact.VideoClipCount); return false; };
            await (Task)Call("RemoveMediaAssetAsync", video.Id, cancel)!;
            Assert.Same(project, Current());
            Assert.Equal(1, prompts);
            Assert.Empty((Stack<KaraokeProject>)Field("_undoProjects").GetValue(window)!);
            await (Task)Call("RemoveMediaAssetAsync", video.Id, (Func<MediaRemovalImpact, bool>)(_ => true))!;
            Assert.Single(Current().MediaAssets);
            Assert.Empty(Current().VideoClips);
            Assert.Equal(project.Lyrics.Lines.Select(line => line.Timing), Current().Lyrics.Lines.Select(line => line.Timing));
            Assert.Single(((ListBox)window.FindName("MediaLibraryList")).Items.Cast<object>());
            Assert.Equal(proxy, previews[video.Id]);
            Assert.Equal(thumbnail, thumbnails[video.Id]);
            await (Task)Call("RestoreHistoryAsync", false)!;
            Assert.Equal(2, Current().MediaAssets.Count);
            Assert.Equal(project.VideoClips, Current().VideoClips);
            Assert.Equal(project.Lyrics.Lines.Select(line => line.Timing), Current().Lyrics.Lines.Select(line => line.Timing));
            Assert.Equal(project.MasterAudioAssetId, Current().MasterAudioAssetId);
            Assert.Equal(proxy, previews[video.Id]);
            object restoredLibraryItem = ((ListBox)window.FindName("MediaLibraryList")).Items.Cast<object>()
                .Single(item => (Guid)item.GetType().GetProperty("Id")!.GetValue(item)! == video.Id);
            Assert.Equal(thumbnail, restoredLibraryItem.GetType().GetProperty("ThumbnailPath")!.GetValue(restoredLibraryItem));
            await (Task)Call("RemoveMediaAssetAsync", unused.Id, (Func<MediaRemovalImpact, bool>)(_ => throw new Exception("Unused media must not prompt")))!;
            Assert.Single(Current().MediaAssets);
            Assert.Single(Current().VideoClips);

            KaraokeProject Current() => ((WorkspaceSession)Field("_workspace").GetValue(window)!).Project;
            object? Call(string name, params object?[] args) => Method(name).Invoke(window, args);
        }
        finally { Field("_workspace").SetValue(window, null); window.Close(); }
    });

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static async Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                Task task = action();
                var frame = new DispatcherFrame();
                var timeout = DateTime.UtcNow.AddSeconds(40);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow >= timeout) { frame.Continue = false; } };
                timer.Start();
                try { Dispatcher.PushFrame(frame); }
                finally { timer.Stop(); }
                Assert.True(task.IsCompleted, "The media-removal workflow did not finish.");
                task.GetAwaiter().GetResult();
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}
