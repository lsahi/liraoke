using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.App.Dialogs;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Tests;

public sealed class AiInterfaceTests
{
    [Fact]
    public async Task ChatRepliesStayStubbedAndOverlayDoesNotResizeEditor()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            string root = Path.Combine(Path.GetTempPath(), "Liraoke.AiUiTests", Guid.NewGuid().ToString("N"));
            try
            {
                window = new MainWindow(new UserPreferenceStore(root)); window.Show(); window.UpdateLayout();
                var chat = (AiChatPanel)window.FindName("AssistantPanel");
                var input = (TextBox)chat.FindName("MessageInput");
                var messages = (StackPanel)chat.FindName("Messages");
                var monitor = (FrameworkElement)window.FindName("PreviewMonitorHost");
                var timeline = (FrameworkElement)window.FindName("ProjectTimeline");
                var before = (monitor.ActualWidth, monitor.ActualHeight, timeline.ActualWidth, timeline.ActualHeight);
                ((Button)window.FindName("AiOpenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                Assert.Equal(Visibility.Visible, chat.Visibility);
                Assert.False(((FrameworkElement)window.FindName("PreviewSettingsPanel")).IsEnabled);
                Assert.Equal(before, (monitor.ActualWidth, monitor.ActualHeight, timeline.ActualWidth, timeline.ActualHeight));
                Assert.Equal(((FrameworkElement)window.FindName("PreviewSettingsPanel")).ActualWidth, chat.Width, 2);
                foreach (string request in new[] { "你好", "更改字幕颜色", "发送请求" })
                {
                    input.Text = request;
                    typeof(AiChatPanel).GetMethod("Send", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(chat, null);
                    Assert.Equal("AI暂未接入", ((TextBlock)messages.Children[^1]).Text);
                }
                input.Text = "草稿";
                typeof(AiChatPanel).GetMethod("Collapse_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(chat, [chat, new RoutedEventArgs()]);
                Assert.Equal(Visibility.Collapsed, chat.Visibility);
                Assert.True(((FrameworkElement)window.FindName("PreviewSettingsPanel")).IsEnabled);
                ((Button)window.FindName("AiFloatingButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("草稿", input.Text); Assert.Equal(7, messages.Children.Count);
                var pet = (AiPetImage)window.FindName("AiPet");
                var launcher = (Button)window.FindName("AiFloatingButton");
                launcher.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Collapsed, chat.Visibility);
                launcher.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Visible, chat.Visibility); Assert.Equal("草稿", input.Text);
                void Gesture(string method, params object[] arguments) => typeof(MainWindow)
                    .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments);
                Gesture("BeginAiPetDrag", new Point(40, 40)); Gesture("MoveAiPetDrag", new Point(240, 160));
                Assert.Equal("trot", pet.CurrentAction);
                Gesture("EndAiPetDrag", false);
                Assert.Equal(Visibility.Visible, chat.Visibility); Assert.Equal("idle-blink", pet.CurrentAction);
                Gesture("BeginAiPetDrag", new Point(40, 40)); Gesture("EndAiPetDrag", true);
                Assert.Equal(Visibility.Visible, chat.Visibility);
                Gesture("BeginAiPetDrag", new Point(40, 40)); Gesture("EndAiPetDrag", false);
                Assert.Equal(Visibility.Collapsed, chat.Visibility); Assert.Equal("happy-hop", pet.CurrentAction);
                launcher.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => pet.AnimationsReady);
                Assert.Equal(256, ((BitmapSource)pet.Source).PixelWidth);
                Assert.True(pet.Source.IsFrozen);
                if (SystemParameters.ClientAreaAnimation)
                {
                    pet.SetDragging(true); var first = pet.Source;
                    PumpUntil(() => !ReferenceEquals(first, pet.Source));
                    pet.SetDragging(false); pet.Greet(); PumpUntil(() => pet.CurrentAction == "idle-blink");
                    window.WindowState = WindowState.Minimized;
                    Assert.False(pet.IsAnimationRunning);
                    window.WindowState = WindowState.Normal; window.UpdateLayout();
                    PumpUntil(() => pet.IsAnimationRunning);
                }
                foreach (var size in new[] { new Size(1440, 940), new Size(1100, 760) })
                {
                    window.Width = size.Width; window.Height = size.Height; window.UpdateLayout();
                    typeof(MainWindow).GetMethod("PlaceAiButton", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [99999d, -99999d]);
                    var button = (Button)window.FindName("AiFloatingButton"); var canvas = (Canvas)window.FindName("AiOverlayLayer");
                    Assert.True(Canvas.GetLeft(button) + button.ActualWidth <= canvas.ActualWidth);
                    Assert.True(Canvas.GetTop(button) >= 0);
                    Assert.True(((ScrollViewer)chat.FindName("ConversationScroll")).ActualHeight >= 40);
                    typeof(MainWindow).GetMethod("PlaceAiButton", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window,
                        [canvas.ActualWidth - button.ActualWidth - 20, canvas.ActualHeight - button.ActualHeight - 16]);
                    Assert.True(Canvas.GetTop(button) + button.ActualHeight <= ((FrameworkElement)window.Content).ActualHeight);
                    Snapshot((FrameworkElement)window.Content, $"ai-preview-{size.Width}.png");
                    ((TabControl)window.FindName("EditorTabs")).SelectedIndex = 1; window.UpdateLayout();
                    Snapshot((FrameworkElement)window.Content, $"ai-timing-{size.Width}.png");
                    ((TabControl)window.FindName("EditorTabs")).SelectedIndex = 0;
                }
                var config = new AiConfigurationWindow(new UserPreferenceStore(root));
                config.Show(); config.UpdateLayout();
                var save = typeof(AiConfigurationWindow).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!;
                save.Invoke(config, null);
                Assert.False(File.Exists(Path.Combine(root, "user-preferences.json")));
                T Field<T>(string name) => (T)typeof(AiConfigurationWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(config)!;
                Field<TextBox>("_name").Text = "示例中转站"; Field<ComboBox>("_official").SelectedIndex = 1;
                Field<TextBox>("_path").Text = "https://example.invalid/v1";
                Field<PasswordBox>("_apiKey").Password = "dummy-configuration-test-key";
                save.Invoke(config, null);
                var provider = Assert.Single(new UserPreferenceStore(root).Load().AiProviders);
                Assert.False(provider.IsOfficial);
                Assert.Equal("dummy-configuration-test-key", UserPreferenceStore.ReadAiCredentials(provider).APIKey);
                config.UpdateLayout(); Snapshot((FrameworkElement)config.Content, "ai-configuration.png"); config.Close();
                window.Close(); Assert.False(pet.IsAnimationRunning); window = null;
                done.SetResult();
            }
            catch (Exception e) { done.SetException(e); }
            finally
            {
                if (window is not null) typeof(MainWindow).GetField("_allowClose", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
                window?.Close(); if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
    private static void Snapshot(FrameworkElement content, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR"); if (directory is null) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
    }
    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Animation did not reach the expected state.");
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }
}
