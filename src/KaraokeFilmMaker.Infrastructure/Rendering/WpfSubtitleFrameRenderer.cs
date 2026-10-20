using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Infrastructure.Rendering;

public sealed class WpfSubtitleFrameRenderer : ISubtitleFrameRenderer, IAsyncDisposable
{
    private readonly KaraokeProject _project;
    private readonly AudioVisualizationData? _audioVisualization;
    private readonly WpfAudioVisualizationPainter _audioVisualizationPainter = new();
    private readonly WpfSubtitlePainter _painter = new();
    private readonly WpfSongTitlePainter _titlePainter = new();
    private readonly SubtitleSceneBuilder _sceneBuilder = new();
    private readonly TaskCompletionSource<Dispatcher> _dispatcherReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    private DrawingVisual? _visual;
    private RenderTargetBitmap? _bitmap;

    public WpfSubtitleFrameRenderer(KaraokeProject project, AudioVisualizationData? audioVisualization = null)
    {
        _project = project;
        _audioVisualization = audioVisualization;
        var thread = new Thread(() =>
        {
            try
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                _dispatcherReady.TrySetResult(dispatcher);
                Dispatcher.Run();
                _stopped.TrySetResult();
            }
            catch (Exception exception)
            {
                _dispatcherReady.TrySetException(exception);
                _stopped.TrySetException(exception);
            }
        }) { IsBackground = true, Name = "Subtitle frame renderer" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public async ValueTask<RenderedFrame> RenderAsync(
        ProjectTime projectTime,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width <= 0 || height <= 0 || Math.Max(width, height) > 1920 || Math.Min(width, height) > 1080)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "字幕帧尺寸必须为正数，长边不超过 1920，短边不超过 1080。");
        }

        Dispatcher dispatcher = await _dispatcherReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await dispatcher.InvokeAsync(() => RenderFrame(projectTime, width, height),
            DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(false);
    }

    private RenderedFrame RenderFrame(ProjectTime time, int width, int height)
    {
        DrawingVisual visual = _visual ??= new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            if (_project.AudioVisualizationEnabled)
            {
                _audioVisualizationPainter.Draw(context, _audioVisualization, time, width, height, _project.AudioVisualizationStyle);
            }
            _painter.Draw(context, _sceneBuilder.Build(_project.Lyrics, time), _project.SubtitleStyle, width, height, _project.Roles);
            _titlePainter.Draw(context, _project, width, height);
        }

        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        }
        RenderTargetBitmap bitmap = _bitmap;
        bitmap.Clear();
        bitmap.Render(visual);
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        bitmap.CopyPixels(pixels, stride, 0);
        // WPF produces premultiplied alpha; the media boundary uses straight-alpha BGRA.
        for (var index = 0; index < pixels.Length; index += 4)
        {
            int alpha = pixels[index + 3];
            if (alpha is > 0 and < 255)
            {
                pixels[index] = (byte)Math.Min(255, (pixels[index] * 255 + alpha / 2) / alpha);
                pixels[index + 1] = (byte)Math.Min(255, (pixels[index + 1] * 255 + alpha / 2) / alpha);
                pixels[index + 2] = (byte)Math.Min(255, (pixels[index + 2] * 255 + alpha / 2) / alpha);
            }
        }

        return new RenderedFrame(width, height, stride, pixels);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Dispatcher dispatcher = await _dispatcherReady.Task.ConfigureAwait(false);
        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        await _stopped.Task.ConfigureAwait(false);
    }
}
