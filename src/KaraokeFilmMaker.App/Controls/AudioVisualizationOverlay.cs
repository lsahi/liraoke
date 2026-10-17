using System.Windows;
using System.Windows.Media;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Infrastructure.Rendering;

namespace KaraokeFilmMaker.App.Controls;

public sealed class AudioVisualizationOverlay : FrameworkElement
{
    private readonly WpfAudioVisualizationPainter _painter = new();
    private AudioVisualizationData? _data;
    private ProjectTime _time;
    private AudioVisualizationStyle _style;

    public void Update(AudioVisualizationData? data, ProjectTime time, AudioVisualizationStyle style = AudioVisualizationStyle.Classic)
    {
        if (ReferenceEquals(data, _data) && style == _style && (data is null || time == _time)) { return; }
        _data = data; _time = time; _style = style; InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_data is not null) { _painter.Draw(drawingContext, _data, _time, ActualWidth, ActualHeight, _style); }
    }
}
