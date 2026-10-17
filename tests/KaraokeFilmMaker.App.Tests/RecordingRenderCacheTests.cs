using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using Xunit.Abstractions;

namespace KaraokeFilmMaker.App.Tests;

public sealed class RecordingRenderCacheTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MovingPlayheadReusesStaticTracksAndProjectEditInvalidatesCache()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var project = KaraokeProject.Create("长歌词", DateTimeOffset.UtcNow) with
                { Lyrics = new LyricImportService().Parse(string.Join('\n', Enumerable.Repeat(new string('歌', 25), 200)), ".txt", LyricLanguage.Chinese) };
                var control = new ProjectTimelineControl();
                control.Update(project, ProjectTime.Zero);
                control.Measure(new Size(1200, 160)); control.Arrange(new Rect(0, 0, 1200, 160));
                var method = typeof(ProjectTimelineControl).GetMethod("OnRender", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var cache = typeof(ProjectTimelineControl).GetField("_staticDrawing", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var visual = new DrawingVisual();
                void Render() { using var dc = visual.RenderOpen(); method.Invoke(control, [dc]); }
                cache.SetValue(control, null);
                var watch = Stopwatch.StartNew(); Render(); watch.Stop();
                double cold = watch.Elapsed.TotalMilliseconds;
                var first = (DrawingGroup)cache.GetValue(control)!; Assert.True(first.IsFrozen);
                watch.Restart();
                for (int frame = 1; frame <= 100; frame++)
                {
                    control.Update(project, ProjectTime.FromSeconds(frame / 24d)); Render();
                    Assert.Same(first, cache.GetValue(control));
                }
                watch.Stop();
                output.WriteLine($"5,000 units: cold track build {cold:F2} ms; cached UI drawing {watch.Elapsed.TotalMilliseconds / 100:F3} ms/frame (not end-to-end latency).");
                control.Update(project with { Lyrics = project.Lyrics with { Lines = [] } }, ProjectTime.Zero); Render();
                Assert.NotSame(first, cache.GetValue(control));
                var edited = cache.GetValue(control); control.ShowLyricUnits = false; Render();
                Assert.NotSame(edited, cache.GetValue(control));
                finished.SetResult();
            }
            catch (Exception exception) { finished.SetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
