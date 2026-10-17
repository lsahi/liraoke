using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;
using KaraokeFilmMaker.Application.Timeline;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private Guid? _previewClipId;
    private Guid? _previewAssetId;
    private bool _videoReady;
    private bool _videoPlaying;
    private TimeSpan? _lastRequestedVideoTime;
    private readonly HashSet<Guid> _failedPreviewAssets = [];

    private void RefreshVideoPreview(ProjectTime position)
    {
        if (_isBusy)
        {
            return;
        }

        VideoClip? clip = _workspace is null || (!IsPreviewMode && !IsRecordingMode) ? null
            : IsRecordingMode ? RecordingMediaPreview.At(_workspace.Project, position)
            : VideoTimelineService.VisibleClipAt(_workspace.Project, position);
        if (clip is null || _failedPreviewAssets.Contains(clip.MediaAssetId))
        {
            ClearVideoPreview();
            return;
        }

        if (_previewClipId != clip.Id)
        {
            ClearVideoPreview();
            var asset = _workspace!.Project.MediaAssets.FirstOrDefault(item => item.Id == clip.MediaAssetId);
            if (asset is null)
            {
                return;
            }

            _previewClipId = clip.Id;
            _previewAssetId = asset.Id;
            if (asset.Kind == MediaKind.Image)
            {
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(_previewPaths.TryGetValue(asset.Id, out string? still) ? still : GetAssetAbsolutePath(asset));
                    image.EndInit(); image.Freeze();
                    StillImagePreview.Source = image;
                    StillImagePreview.Visibility = Visibility.Visible;
                }
                catch (Exception error) when (error is System.IO.IOException or NotSupportedException or ArgumentException)
                {
                    _failedPreviewAssets.Add(asset.Id);
                    ClearVideoPreview();
                    StatusText.Text = $"图片预览失败：{error.Message}";
                }
                return;
            }
            VideoPreview.Source = new Uri(_previewPaths.TryGetValue(asset.Id, out string? proxy) ? proxy : GetAssetAbsolutePath(asset));
            VideoPreview.Play();
            VideoPreview.Pause();
            return;
        }

        if (!_videoReady)
        {
            return;
        }

        TimeSpan expected = TimeSpan.FromSeconds(
            clip.SourceStart.TotalSeconds + (position - clip.TimelineStart).TotalSeconds);
        if ((!_playback.IsPlaying && _lastRequestedVideoTime != expected)
            || (_playback.IsPlaying && Math.Abs((VideoPreview.Position - expected).TotalMilliseconds) > 120))
        {
            VideoPreview.Position = expected;
            _lastRequestedVideoTime = expected;
        }

        if (_videoPlaying != _playback.IsPlaying)
        {
            if (_playback.IsPlaying)
            {
                VideoPreview.Play();
            }
            else
            {
                VideoPreview.Pause();
            }

            _videoPlaying = _playback.IsPlaying;
        }
    }

    private void ClearVideoPreview()
    {
        StillImagePreview.Source = null;
        StillImagePreview.Visibility = Visibility.Collapsed;
        if (_previewClipId is null)
        {
            return;
        }

        VideoPreview.Close();
        VideoPreview.Source = null;
        VideoPreview.Visibility = Visibility.Hidden;
        _previewClipId = null;
        _previewAssetId = null;
        _videoReady = false;
        _videoPlaying = false;
        _lastRequestedVideoTime = null;
    }

    private void VideoPreview_MediaOpened(object sender, RoutedEventArgs e)
    {
        _videoReady = true;
        VideoPreview.Visibility = Visibility.Visible;
        RefreshVideoPreview(_playback.Position);
    }

    private void VideoPreview_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (_previewAssetId is { } assetId)
        {
            _failedPreviewAssets.Add(assetId);
        }

        ClearVideoPreview();
        StatusText.Text = $"视频预览失败：{e.ErrorException.Message}";
    }
}
