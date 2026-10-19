using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Separation;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Separation;

namespace KaraokeFilmMaker.App.Tests;

public sealed class VocalSeparationWindowTests
{
    [Theory]
    [InlineData("unsupported", false, false)]
    [InlineData("old-nvidia", false, false)]
    [InlineData("ready", true, false)]
    [InlineData("installed", true, true)]
    public async Task GpuControlsExplainDisabledStateAndKeepCpuUsable(string state, bool supported, bool installed)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "Liraoke-SeparationUi-" + Guid.NewGuid().ToString("N"));
            VocalSeparationWindow? window = null;
            try
            {
                string bundle = Path.Combine(root, "bundle"), components = Path.Combine(root, "components");
                Directory.CreateDirectory(Path.Combine(bundle, "cpu")); Directory.CreateDirectory(Path.Combine(bundle, "model"));
                foreach (string name in new[] { "cpu/python.exe", "cpu/runtime.json", "model/955717e8.safetensors", "demucs-worker.py", "song.wav" })
                    File.WriteAllText(Path.Combine(bundle, name), "placeholder");
                var runtime = new DemucsRuntimeManager(bundle, components);
                if (installed)
                {
                    string leaf = runtime.Manifest.Id + "-" + Guid.NewGuid().ToString("N");
                    string path = Path.Combine(components, leaf); Directory.CreateDirectory(path);
                    File.WriteAllText(Path.Combine(path, "python.exe"), "placeholder");
                    File.WriteAllText(Path.Combine(path, "runtime.json"), JsonSerializer.Serialize(new CpuRuntimeInfo(runtime.Manifest.TorchVersion, "4.1.0", new string('0', 64))));
                    File.WriteAllText(Path.Combine(components, "active.json"), JsonSerializer.Serialize(leaf));
                }
                window = new(Path.Combine(bundle, "song.wav"), "ユミル · 原歌曲保留", Path.Combine(root, "outputs"), new("ffmpeg", "ffprobe"), runtime);
                GpuAvailability gpu = state switch
                {
                    "unsupported" => GpuSupportPolicy.Evaluate([]),
                    "old-nvidia" => GpuSupportPolicy.Evaluate([new(0, "GTX 1080", 6, 1, 12080)]),
                    _ => GpuSupportPolicy.Evaluate([new(0, "NVIDIA RTX 5080", 12, 0, 12080)]),
                };
                typeof(VocalSeparationWindow).GetField("_gpu", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, gpu);
                typeof(VocalSeparationWindow).GetMethod("RefreshState", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                var install = (Button)window.FindName("InstallGpuButton");
                Assert.Equal(supported, install.IsEnabled);
                Assert.NotNull(install.ToolTip);
                Assert.True(((Button)window.FindName("GenerateButton")).IsEnabled);
                Assert.Equal(installed, ((Button)window.FindName("UninstallButton")).IsEnabled);
                Assert.Equal(installed, ((ComboBoxItem)window.FindName("GpuEngineItem")).IsEnabled);
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(606, 530)); content.Arrange(new Rect(0, 0, 606, 530)); content.UpdateLayout();
                string? snapshots = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
                if (!string.IsNullOrWhiteSpace(snapshots))
                {
                    Directory.CreateDirectory(snapshots);
                    var image = new RenderTargetBitmap(606, 530, 96, 96, PixelFormats.Pbgra32); image.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var output = File.Create(Path.Combine(snapshots, "separation-" + state + ".png")); encoder.Save(output);
                }
                finished.TrySetResult();
            }
            catch (Exception e) { finished.TrySetException(e); }
            finally { window?.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
