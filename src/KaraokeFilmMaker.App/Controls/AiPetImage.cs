using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KaraokeFilmMaker.App.Controls;

/// <summary>Small, shared, frozen PNG frames; no browser, video player or desktop window.</summary>
public sealed class AiPetImage : Image
{
    private const string AssetRoot = "pack://application:,,,/Liraoke;component/Assets/AiPet/";
    private sealed record AnimationDefinition(int FrameDurationMilliseconds, string[] Frames);
    private sealed record Animation(int FrameDurationMilliseconds, BitmapSource[] Frames);
    private static readonly Lazy<Task<IReadOnlyDictionary<string, Animation>>> SharedAnimations = new(() => Task.Run(LoadAnimations));
    private static readonly Lazy<BitmapSource> RestImage = new(() => Decode("Rest.png"));
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private readonly Stopwatch _clock = new();
    private IReadOnlyDictionary<string, Animation>? _animations;
    private Window? _host;
    private string _action = "idle-blink";
    private bool _dragging;
    public bool AnimationsReady => _animations is not null;
    public bool IsAnimationRunning => _timer.IsEnabled;
    public string CurrentAction => _action;

    public AiPetImage()
    {
        Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Source = RestImage.Value;
        _timer.Tick += (_, _) => RenderFrame();
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _timer.Stop(); _clock.Stop();
            if (_host is not null) _host.StateChanged -= HostStateChanged;
            _host = null;
        };
        IsVisibleChanged += (_, _) => UpdatePlayback();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_host is not null) _host.StateChanged -= HostStateChanged;
        _host = Window.GetWindow(this);
        if (_host is not null) _host.StateChanged += HostStateChanged;
        try
        {
            _animations = await SharedAnimations.Value;
            RenderFrame(); UpdatePlayback();
        }
        catch (Exception error) when (error is IOException or JsonException or NotSupportedException or ArgumentException)
        {
            // A static supplied image remains a usable chat launcher if animation decoding fails.
            _timer.Stop(); _clock.Stop();
        }
    }

    private void HostStateChanged(object? sender, EventArgs e) => UpdatePlayback();
    private void UpdatePlayback()
    {
        if (!IsLoaded || !IsVisible || _host?.WindowState == WindowState.Minimized || !SystemParameters.ClientAreaAnimation || _animations is null)
        { _timer.Stop(); _clock.Stop(); return; }
        _timer.Interval = TimeSpan.FromMilliseconds(_animations[_action].FrameDurationMilliseconds);
        _clock.Start(); _timer.Start();
    }

    public void SetDragging(bool dragging)
    {
        _dragging = dragging;
        SetAction(dragging ? "trot" : "idle-blink");
    }
    public void Greet() { if (!_dragging) SetAction("happy-hop"); }
    private void SetAction(string action)
    {
        _action = action; _clock.Reset(); RenderFrame(); UpdatePlayback();
    }
    private void RenderFrame()
    {
        if (_animations is null) return;
        Animation animation = _animations[_action];
        long frame = (long)_clock.Elapsed.TotalMilliseconds / animation.FrameDurationMilliseconds;
        if (_action == "happy-hop" && frame >= animation.Frames.Length)
        { _action = "idle-blink"; _clock.Restart(); animation = _animations[_action]; frame = 0; UpdatePlayback(); }
        Source = animation.Frames[(int)(frame % animation.Frames.Length)];
    }

    private static IReadOnlyDictionary<string, Animation> LoadAnimations()
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri(AssetRoot + "animations.json"))!.Stream;
        var definitions = JsonSerializer.Deserialize<Dictionary<string, AnimationDefinition>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("桌宠动画清单为空。");
        var images = new Dictionary<string, BitmapSource>(StringComparer.Ordinal);
        var result = new Dictionary<string, Animation>(StringComparer.Ordinal);
        foreach (string name in new[] { "idle-blink", "trot", "happy-hop" })
        {
            if (!definitions.TryGetValue(name, out var definition) || definition.Frames is not { Length: > 0 and <= 120 }
                || definition.FrameDurationMilliseconds is < 30 or > 1000)
                throw new InvalidDataException("桌宠动画清单无效。");
            var frames = definition.Frames.Select(file =>
            {
                if (!images.TryGetValue(file, out var bitmap)) { bitmap = Decode(file); images.Add(file, bitmap); }
                return bitmap;
            }).ToArray();
            result.Add(name, new Animation(definition.FrameDurationMilliseconds, frames));
        }
        return result;
    }

    private static BitmapSource Decode(string file)
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri(AssetRoot + file))!.Stream;
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 256;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
}
