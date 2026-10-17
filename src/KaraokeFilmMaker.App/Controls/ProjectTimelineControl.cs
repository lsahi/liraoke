using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Audio;

namespace KaraokeFilmMaker.App.Controls;

public sealed record TimelineDrag(Guid ItemId, Guid? LineId, ProjectTime Start, ProjectTime SourceStart,
    ProjectDuration Duration, int TrackIndex = 0);

public sealed class ProjectTimelineControl : FrameworkElement
{
    private const double Gutter = 132, Ruler = 26, LyricHeight = 30, VideoHeight = 40, SongHeight = 40;
    private KaraokeProject? _project;
    private ProjectTime _position;
    private WaveformData? _waveform;
    private double _zoom = 1, _viewStart, _dragTime;
    private TimelineDrag? _drag, _original;
    private int _handle;
    private DrawingGroup? _staticDrawing;
    private (KaraokeProject? Project, WaveformData? Waveform, bool Units, Size Size, double Dpi,
        double Zoom, double Start, TimelineDrag? Drag, Guid? Selected, int Track) _drawingKey;

    public ProjectTimelineControl()
    {
        Focusable = true;
        ClipToBounds = true;
        ToolTip = "拖动视频 / 图片片段移动，拖边缘调整时长。滚轮缩放，Shift+滚轮平移，Alt 拖动关闭片段吸附。";
        Height = SongTop + SongHeight + 8;
    }

    public event Action<double>? SeekRequested;
    public event Action<TimelineDrag>? EditRequested;
    public event Action<Guid?>? ClipSelected;
    public Guid? SelectedClipId { get; private set; }
    public int SelectedTrackIndex { get; private set; }
    public bool ShowLyricUnits { get; set; } = true;
    public WaveformData? Waveform
    {
        get => _waveform;
        set
        {
            if (value is null || _waveform?.DurationSeconds != value.DurationSeconds) { _zoom = 1; _viewStart = 0; }
            _waveform = value; InvalidateVisual();
        }
    }

    public void Update(KaraokeProject? project, ProjectTime position)
    {
        _project = project; _position = position;
        SelectedTrackIndex = 0;
        if (SelectedClipId is { } id && project?.VideoClips.Any(clip => clip.Id == id) != true) { SelectedClipId = null; }
        double height = SongTop + SongHeight + 8;
        if (Height != height) { Height = height; }
        InvalidateVisual();
    }

    public void SelectClip(Guid? id)
    {
        SelectedClipId = id;
        SelectedTrackIndex = 0;
        InvalidateVisual();
    }

    public void FitToProject() { _zoom = 1; _viewStart = 0; InvalidateVisual(); }
    private double VideoTop => Ruler + LyricHeight;
    private double SongTop => VideoTop + VideoHeight;
    private double Duration => Math.Max(1, Math.Max(_waveform?.DurationSeconds ?? 0,
        Math.Max(_project?.MediaAssets.FirstOrDefault(asset => asset.Id == _project.MasterAudioAssetId)?.Duration.TotalSeconds ?? 0,
            _project?.VideoClips.Select(clip => clip.TimelineEnd.TotalSeconds).DefaultIfEmpty(0).Max() ?? 0)));
    private double ViewDuration => Duration / _zoom;
    private double ContentWidth => Math.Max(1, ActualWidth - Gutter);
    private double X(double seconds) => Gutter + (seconds - _viewStart) * ContentWidth / ViewDuration;
    private double Seconds(double x) => Math.Clamp(_viewStart + (x - Gutter) * ViewDuration / ContentWidth, 0, Duration);
    private int? TrackAt(double y)
    {
        return y >= VideoTop && y < SongTop ? 0 : null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= Gutter) { return; }
        var key = (_project, _waveform, ShowLyricUnits, RenderSize, VisualTreeHelper.GetDpi(this).PixelsPerDip,
            _zoom, _viewStart, _drag, SelectedClipId, SelectedTrackIndex);
        if (_staticDrawing is null || _drawingKey != key)
        {
            var drawing = new DrawingGroup();
            using (var context = drawing.Open()) { DrawTracks(context); }
            drawing.Freeze(); _staticDrawing = drawing; _drawingKey = key;
        }
        dc.DrawDrawing(_staticDrawing);
        dc.PushClip(new RectangleGeometry(new Rect(Gutter, 0, ContentWidth, ActualHeight)));
        double head = X(_position.TotalSeconds);
        dc.DrawLine(new Pen(Brush("#B38A32"), 2), new Point(head, 0), new Point(head, ActualHeight));
        dc.DrawRectangle(Brush("#B38A32"), null, new Rect(head - 4, 0, 8, 10));
        dc.Pop();
    }

    private void DrawTracks(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        Lane(dc, "字幕", Ruler, LyricHeight, "#F5FAFD");
        Lane(dc, "视频 / 图片", VideoTop, VideoHeight, "#FFFCF4");
        Lane(dc, "歌曲 / 声音", SongTop, SongHeight, "#F5FAFD");
        dc.PushClip(new RectangleGeometry(new Rect(Gutter, 0, ContentWidth, ActualHeight)));
        double requestedStep = Math.Max(0.01, ViewDuration * 90 / ContentWidth);
        double power = Math.Pow(10, Math.Floor(Math.Log10(requestedStep)));
        double factor = requestedStep / power;
        double step = (factor <= 1 ? 1 : factor <= 2 ? 2 : factor <= 5 ? 5 : 10) * power;
        for (double tick = Math.Ceiling(_viewStart / step) * step; tick <= _viewStart + ViewDuration; tick += step)
        {
            double x = X(tick);
            dc.DrawLine(new Pen(Brush("#E5EDF1"), 0.5), new Point(x, 0), new Point(x, ActualHeight));
            Label(dc, TimeSpan.FromSeconds(tick).ToString(tick >= 60 ? @"m\:ss" : @"ss\.ff"), x + 4, 5, Brush("#687782"));
        }
        if (_waveform is { } waveform)
        {
            var pen = new Pen(Brush("#81B9CD"), 1);
            for (int pixel = (int)Gutter; pixel < (int)ActualWidth; pixel++)
            {
                int start = (int)(Seconds(pixel) * waveform.BinsPerSecond);
                int end = Math.Min(waveform.Peaks.Length, Math.Max(start + 1, (int)(Seconds(pixel + 1) * waveform.BinsPerSecond)));
                float peak = 0;
                for (int index = Math.Max(0, start); index < end; index++) { peak = Math.Max(peak, waveform.Peaks[index]); }
                double amplitude = Math.Min(1, peak) * (SongHeight / 2 - 4);
                dc.DrawLine(pen, new Point(pixel, SongTop + SongHeight / 2 - amplitude), new Point(pixel, SongTop + SongHeight / 2 + amplitude));
            }
        }
        if (_project is { } project)
        {
            if (ShowLyricUnits)
            {
                foreach (var line in project.Lyrics.Lines)
                foreach (var unit in line.Units)
                {
                    DrawItem(dc, unit.Id, line.Id, unit.Timing.Start.TotalSeconds, unit.Timing.Duration.TotalSeconds,
                        unit.Reading, Ruler + 3, LyricHeight - 6, Brush("#EDF7FC"));
                }
            }
            else
            {
                foreach (var line in project.Lyrics.Lines)
                    DrawItem(dc, line.Id, line.Id, line.Timing.Start.TotalSeconds, line.Timing.Duration.TotalSeconds,
                        line.PrimaryText, Ruler + 3, LyricHeight - 6, Brush("#EDF7FC"));
            }
            foreach (var clip in project.VideoClips)
            {
                var asset = project.MediaAssets.FirstOrDefault(asset => asset.Id == clip.MediaAssetId);
                string name = asset?.DisplayName ?? "缺失素材";
                DrawItem(dc, clip.Id, null, clip.TimelineStart.TotalSeconds, clip.Duration.TotalSeconds,
                    name + (asset?.Kind == MediaKind.Image ? " · 图片" : clip.IsMuted ? " · 静音" : " · 原声"), VideoTop + 4, VideoHeight - 8,
                    Brush(clip.IsMuted ? "#F7F8FA" : "#FFFBEF"));
            }
            if (!ShowLyricUnits && project.WorkArea is { } area)
            {
                double left = Math.Clamp(X(area.Start.TotalSeconds), Gutter, ActualWidth);
                double right = Math.Clamp(X(area.End.TotalSeconds), Gutter, ActualWidth);
                var excluded = Brush("#88717D86");
                dc.DrawRectangle(excluded, null, new Rect(Gutter, Ruler, left - Gutter, ActualHeight - Ruler));
                dc.DrawRectangle(excluded, null, new Rect(right, Ruler, ActualWidth - right, ActualHeight - Ruler));
                var boundary = new Pen(Brush("#3296B6"), 2);
                foreach (double time in new[] { area.Start.TotalSeconds, area.End.TotalSeconds })
                {
                    double edge = X(time);
                    dc.DrawLine(boundary, new Point(edge, 0), new Point(edge, ActualHeight));
                }
            }
        }
        dc.Pop();
        Label(dc, "时间 / 秒", 12, 5, Brush("#687782"));
    }

    private void Lane(DrawingContext dc, string label, double top, double height, string color)
    {
        dc.DrawRectangle(Brush(color), null, new Rect(0, top, Gutter, height));
        dc.DrawLine(new Pen(Brush("#E3ECF1"), 1), new Point(0, top + height), new Point(ActualWidth, top + height));
        Label(dc, label, 12, top + (height - 14) / 2, Brush("#4A626F"));
    }

    private void DrawItem(DrawingContext dc, Guid id, Guid? lineId, double start, double duration,
        string text, double top, double height, Brush brush)
    {
        if (_drag is { } drag && drag.ItemId == id && drag.LineId == lineId)
        { start = drag.Start.TotalSeconds; duration = drag.Duration.TotalSeconds; brush = Brush("#FFF3CF"); }
        double x = X(start), width = Math.Max(3, X(start + duration) - x);
        var rect = new Rect(x, top, width, height);
        if (rect.Right < Gutter || rect.Left > ActualWidth) { return; }
        dc.DrawRoundedRectangle(brush, new Pen(id == SelectedClipId ? Brush("#78ACC2") : Brush("#BFD3DD"), id == SelectedClipId ? 2 : 0.75), rect, 4, 4);
        if (width < 18) { return; }
        dc.PushClip(new RectangleGeometry(rect));
        Label(dc, text, Math.Max(Gutter, x) + 7, top + (height - 14) / 2, Brush("#344E5B"));
        dc.Pop();
    }

    private static Brush Brush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
    private void Label(DrawingContext dc, string text, double x, double y, Brush color) => dc.DrawText(
        new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, color,
            VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus();
        _original = null;
        if (_project is null) { return; }
        Point point = e.GetPosition(this);
        int? track = TrackAt(point.Y);
        if (track is { } target) { SelectedTrackIndex = target; }
        if (point.X < Gutter) { InvalidateVisual(); e.Handled = true; return; }
        double time = Seconds(point.X);
        if (track is { } index)
        {
            var clip = _project.VideoClips.FirstOrDefault(item => item.TrackIndex == index
                && time >= item.TimelineStart.TotalSeconds && time < item.TimelineEnd.TotalSeconds);
            SelectClip(clip?.Id); ClipSelected?.Invoke(clip?.Id);
            if (clip is not null) { _original = new TimelineDrag(clip.Id, null, clip.TimelineStart, clip.SourceStart, clip.Duration, clip.TrackIndex); }
        }
        else if (ShowLyricUnits && point.Y >= Ruler && point.Y < VideoTop)
        {
            foreach (var line in _project.Lyrics.Lines)
            {
                var unit = line.Units.FirstOrDefault(item => time >= item.Timing.Start.TotalSeconds && time < item.Timing.End.TotalSeconds);
                if (unit is not null)
                { _original = new TimelineDrag(unit.Id, line.Id, unit.Timing.Start, ProjectTime.Zero, unit.Timing.Duration); break; }
            }
        }
        if (_original is { } original)
        {
            _drag = original; _dragTime = time;
            _handle = Math.Abs(point.X - X(original.Start.TotalSeconds)) < 7 ? -1
                : Math.Abs(point.X - X(original.Start.TotalSeconds + original.Duration.TotalSeconds)) < 7 ? 1 : 0;
            CaptureMouse();
        }
        else { SeekRequested?.Invoke(time); }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || _original is not { } original || _project is null) { return; }
        UpdateDrag(e.GetPosition(this));
    }

    private void UpdateDrag(Point point)
    {
        if (_original is not { } original || _project is null) { return; }
        bool isImage = original.LineId is null && _project.VideoClips.FirstOrDefault(clip => clip.Id == original.ItemId) is { } imageClip
            && _project.MediaAssets.FirstOrDefault(asset => asset.Id == imageClip.MediaAssetId)?.Kind == MediaKind.Image;
        double dragSeconds = isImage ? Math.Max(0, _viewStart + (point.X - Gutter) * ViewDuration / ContentWidth) : Seconds(point.X);
        double delta = dragSeconds - _dragTime;
        double start = original.Start.TotalSeconds, source = original.SourceStart.TotalSeconds, length = original.Duration.TotalSeconds;
        double min = 0, max = Math.Max(Duration, start + length);
        int track = original.TrackIndex;
        if (original.LineId is { } lineId)
        {
            var line = _project.Lyrics.Lines.First(item => item.Id == lineId);
            min = line.Timing.Start.TotalSeconds; max = line.Timing.End.TotalSeconds;
        }
        else if (_handle == 0) { track = TrackAt(point.Y) ?? original.TrackIndex; }
        if (_handle == -1)
        {
            delta = Math.Clamp(delta, Math.Max(min - start, original.LineId is null && !isImage ? -source : min - start), Math.Max(0, length - 0.001));
            start += delta; length -= delta; if (original.LineId is null && !isImage) { source += delta; }
        }
        else if (_handle == 1)
        {
            double maximumLength = max - start;
            if (isImage) { maximumLength = Math.Max(0.001, Math.Max(max - start, length + delta)); }
            else if (original.LineId is null)
            {
                var clip = _project.VideoClips.First(item => item.Id == original.ItemId);
                maximumLength = _project.MediaAssets.First(asset => asset.Id == clip.MediaAssetId).Duration.TotalSeconds - source;
            }
            length = Math.Clamp(length + delta, Math.Min(0.001, maximumLength), maximumLength);
        }
        else
        {
            start = Math.Clamp(start + delta, min, Math.Max(min, max - length));
            if (original.LineId is null && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                double tolerance = 8 * ViewDuration / ContentWidth;
                double[] candidates = _project.VideoClips.Where(clip => clip.Id != original.ItemId && clip.TrackIndex == track)
                    .SelectMany(clip => new[] { clip.TimelineStart.TotalSeconds - length, clip.TimelineEnd.TotalSeconds })
                    .Append(_position.TotalSeconds).Append(0).Where(value => value >= min && value <= max - length).ToArray();
                double nearest = candidates.OrderBy(value => Math.Abs(value - start)).FirstOrDefault(start);
                if (Math.Abs(nearest - start) <= tolerance) { start = nearest; }
            }
        }
        _drag = original with { Start = ProjectTime.FromSeconds(start), SourceStart = ProjectTime.FromSeconds(source),
            Duration = ProjectDuration.FromSeconds(length), TrackIndex = track };
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        TimelineDrag? changed = _drag;
        bool moved = changed is not null && changed != _original;
        _drag = null; _original = null; ReleaseMouseCapture(); InvalidateVisual();
        if (moved) { EditRequested?.Invoke(changed!); }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    { base.OnLostMouseCapture(e); _drag = null; _original = null; InvalidateVisual(); }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double anchor = Seconds(e.GetPosition(this).X);
        if (Keyboard.Modifiers == ModifierKeys.Shift) { _viewStart += (e.Delta > 0 ? -1 : 1) * ViewDuration * 0.1; }
        else
        {
            _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.5 : 1 / 1.5), 1, 128);
            _viewStart = anchor - (e.GetPosition(this).X - Gutter) * ViewDuration / ContentWidth;
        }
        _viewStart = Math.Clamp(_viewStart, 0, Math.Max(0, Duration - ViewDuration));
        e.Handled = true; InvalidateVisual();
    }
}
