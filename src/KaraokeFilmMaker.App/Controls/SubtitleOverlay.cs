using System.Windows;
using System.Windows.Media;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Rendering;

namespace KaraokeFilmMaker.App.Controls;

public sealed class SubtitleOverlay : FrameworkElement
{
    private readonly WpfSubtitlePainter _painter = new();
    private readonly SubtitleSceneBuilder _sceneBuilder = new();
    private KaraokeProject? _project;
    private ProjectTime _time;

    public void Update(KaraokeProject? project, ProjectTime time)
    {
        if (ReferenceEquals(_project, project) && _time == time)
        {
            return;
        }

        _project = project;
        _time = time;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_project is not null)
        {
            _painter.Draw(drawingContext, _sceneBuilder.Build(_project.Lyrics, _time),
                _project.SubtitleStyle, ActualWidth, ActualHeight);
        }
    }
}
