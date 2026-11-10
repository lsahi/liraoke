using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Application.Preferences;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.App.Dialogs;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Tests;

public sealed class AiConnectedInterfaceTests
{
    [Fact]
    public Task TestColorsInvalidateAndCredentialsFollowModel() => Sta(() =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var fake = new FakeChat(); var window = new AiConfigurationWindow(new UserPreferenceStore(root), fake);
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, Field<StackPanel>(window, "_keyField").Visibility);
            var save = Field<Button>(window, "_save"); var original = ((SolidColorBrush)save.Background).Color;
            Field<ComboBox>(window, "_model").SelectedIndex = 0;
            Assert.Equal(Visibility.Visible, Field<StackPanel>(window, "_keyField").Visibility);
            Assert.Equal("deepseek-flash", Field<TextBox>(window, "_name").Text);
            Field<PasswordBox>(window, "_apiKey").Password = "fake";
            var test = (Task)Call(window, "TestAsync")!; Pump(() => test.IsCompleted); test.GetAwaiter().GetResult();
            Assert.Equal(Color.FromRgb(45, 130, 84), ((SolidColorBrush)save.Background).Color);
            Shot(window, "ai-configuration-tested.png");
            Field<TextBox>(window, "_path").Text += "?new=1";
            Assert.Equal(original, ((SolidColorBrush)save.Background).Color);
            fake.Fail = true; test = (Task)Call(window, "TestAsync")!; Pump(() => test.IsCompleted); test.GetAwaiter().GetResult();
            Assert.Equal(Color.FromRgb(247, 205, 91), ((SolidColorBrush)save.Background).Color);
            // Edits during a request must not turn the new configuration green.
            fake.Fail = false; test = (Task)Call(window, "TestAsync")!;
            Field<PasswordBox>(window, "_apiKey").Password = "changed"; Pump(() => test.IsCompleted); test.GetAwaiter().GetResult();
            Assert.Equal(original, ((SolidColorBrush)save.Background).Color);
            Call(window, "Save"); Assert.Single(new UserPreferenceStore(root).Load().AiProviders);
        }
        finally { Field<bool>(window, "_dirty"); typeof(AiConfigurationWindow).GetField("_dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false); window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    [Fact]
    public Task ChatStreamsHistoryAndThinkingAndCanStop() => Sta(() =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new UserPreferenceStore(root); var p = AiModelCatalog.Presets[0];
        store.SaveAiProvider(Guid.NewGuid(), "DeepSeek 示例", true, p.Endpoint, new("fake"), p.Model, p.ProviderId, p.Protocol);
        var fake = new FakeChat { ThinkingGate = new(), BodyGate = new() }; var chat = new AiChatPanel(); chat.Initialize(store, fake);
        var window = new Window { Content = chat, Width = 390, Height = 630 };
        try
        {
            window.Show(); var input = (TextBox)chat.FindName("MessageInput"); var send = (Button)chat.FindName("SendButton");
            input.Text = "你好"; send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var messages = (StackPanel)chat.FindName("Messages");
            Pump(() => messages.Children.OfType<Expander>().Any(e => e.Visibility == Visibility.Visible));
            var thought = messages.Children.OfType<Expander>().Last();
            Assert.True(thought.IsExpanded); Assert.Equal("返回的思考内容", ((TextBox)thought.Content).Text);
            Shot(window, "ai-thinking-before-answer.png");
            fake.ThinkingGate!.SetResult(); fake.ThinkingGate = null;
            Pump(() => !thought.IsExpanded);
            Assert.Contains(messages.Children.OfType<TextBox>(), b => b.Text == "你好，");
            Shot(window, "ai-thinking-auto-collapsed.png");
            thought.IsExpanded = true;
            fake.BodyGate!.SetResult(); fake.BodyGate = null;
            Pump(() => (string)send.Content == "发送");
            Assert.True(thought.IsExpanded); // Later text must not override a manual expansion.
            Assert.Contains(messages.Children.OfType<TextBox>(), b => b.Text == "你好，我是莉拉。" );
            Assert.Contains(messages.Children.OfType<Expander>(), e => e.Visibility == Visibility.Visible && ((TextBox)e.Content).Text == "返回的思考内容");
            fake.ReturnThinking = false;
            input.Text = "继续"; send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(() => (string)send.Content == "发送");
            Assert.Equal(2, fake.LastHistory.Count);
            Assert.Equal(Visibility.Collapsed, messages.Children.OfType<Expander>().Last().Visibility);
            Shot(window, "ai-streamed-conversation.png");
            fake.Block = true; input.Text = "停止测试"; send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(() => (string)send.Content == "发送");
            Assert.True(fake.Cancelled);
            Assert.Contains(messages.Children.OfType<TextBlock>(), b => b.Text.Contains("已停止"));
        }
        finally { chat.Stop(); window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    private sealed class FakeChat : IAiChatService
    {
        public bool Fail, Block, Cancelled;
        public bool ReturnThinking = true;
        public TaskCompletionSource? ThinkingGate, BodyGate;
        public IReadOnlyList<AiChatMessage> LastHistory = [];
        public async IAsyncEnumerable<AiChatEvent> StreamAsync(AiProviderPreference provider, AiCredentials keys,
            IReadOnlyList<AiChatMessage> history, string prompt, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastHistory = history;
            try { await Task.Delay(Block ? 30000 : 30, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            if (Fail) { yield return new("error", "HTTP 401：APIKey 无效"); yield break; }
            if (ReturnThinking) yield return new("thinking", "返回的思考内容");
            if (ThinkingGate is { } thinkingGate) await thinkingGate.Task.WaitAsync(cancellationToken);
            yield return new("text", "你好，");
            if (BodyGate is { } bodyGate) await bodyGate.Task.WaitAsync(cancellationToken);
            await Task.Delay(80, cancellationToken); yield return new("text", "我是莉拉。"); yield return new("done", "");
        }
    }
    private static T Field<T>(object x, string name) => (T)x.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(x)!;
    private static object? Call(object x, string name) => x.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(x, null);
    private static void Pump(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done()) { Assert.True(DateTime.UtcNow < until); var frame = new DispatcherFrame(); var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) }; t.Tick += (_, _) => { t.Stop(); frame.Continue = false; }; t.Start(); Dispatcher.PushFrame(frame); }
    }
    private static Task Sta(Action action)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); action(); result.SetResult(); } catch (Exception e) { result.SetException(e); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }
    private static void Shot(Window window, string name)
    {
        string? dir = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR"); if (dir is null) return;
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var b = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        b.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(b)); Directory.CreateDirectory(dir);
        using var file = File.Create(Path.Combine(dir, name)); encoder.Save(file);
    }
}

