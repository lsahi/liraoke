using System.IO;
using System.Text.Json;
using System.Windows;
using KaraokeFilmMaker.Application.Music;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Infrastructure.Music;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private async void SearchMusic_Click(object sender, RoutedEventArgs e) => await OpenMusicSearchAsync();
    private async Task OpenMusicSearchAsync(string? query = null, CancellationToken token = default)
    {
        if (_workspace is null || _isBusy) { StatusText.Text = "请先打开工作区。"; return; }
        if (!CanLeaveSourceLyricEdit() || !CanLeaveQuickEdit()) return;
        if (_markers.Count > 0 || _undoneMarkers.Count > 0)
        { StatusText.Text = "请先完成当前句并按 Enter 应用打点，再更换歌曲。"; return; }
        try
        {
            if (_playback.IsPlaying) await _playback.PauseAsync();
            VideoPreview.Pause(); PlayPauseButton.Content = "播放";
            var host = new MusicHostClient();
            var sources = new MusicSourceRegistry([new NeteaseMusicSourceAdapter(host), new TencentMusicSourceAdapter(host), new QishuiMusicSourceAdapter(host)]);
            using var client = PublicMediaHttp.CreateClient();
            var search = new MusicSearchWindow(sources, new MusicAudioTransfer(_mediaTools, host, client), _userPreferenceStore) { Owner = this };
            if (query is not null) ((System.Windows.Controls.TextBox)search.FindName("QueryInput")).Text = query;
            using var cancel = token.Register(() => Dispatcher.BeginInvoke(() => { if (search.IsVisible) search.Close(); }));
            token.ThrowIfCancellationRequested();
            if (search.ShowDialog() != true || search.Selection is not { } selected) return;
            token.ThrowIfCancellationRequested();
            using (selected)
            {
                await ImportPreparedMusicAsync(selected, token);
            }
        }
        catch (Exception) { StatusText.Text = "无法打开找歌窗口，请检查音频设备与找歌组件。"; }
    }

    private async Task ImportPreparedMusicAsync(PreparedMusicAudio selected, CancellationToken token)
    {
        if (_workspace is null || _isBusy) throw new InvalidOperationException("请先打开工作区并等待当前任务完成。");
        bool imported = false;
        await RunUiOperationAsync(async () =>
        {
            var operation = _operationCancellation!;
            using var cancel = token.Register(() => operation.Cancel());
                    ApplyLyricRowsToProject(); ApplyVideoRowsToProject();
                    token.ThrowIfCancellationRequested();
                    _workspace = await _workspaceService.ImportMediaAsync(_workspace, selected.AudioPath, MediaKind.Audio, _operationCancellation!.Token);
                    imported = true;
                    var asset = _workspace.Project.MediaAssets.Single(item => item.Id == _workspace.Project.MasterAudioAssetId);
                    bool provenanceSaved = true;
                    try
                    {
                        // Portable non-secret provenance only, next to the copied media file.
                        string sidecar = Path.Combine(_workspace.RootPath, asset.RelativePath) + ".source.json";
                        await File.WriteAllTextAsync(sidecar, JsonSerializer.Serialize(new { source = selected.Track.SourceId,
                            id = selected.Track.Id, title = selected.Track.Title, artist = selected.Track.Artist, album = selected.Track.Album,
                            durationSeconds = selected.DurationSeconds, quality = selected.Quality, importedAtUtc = DateTimeOffset.UtcNow }));
                    }
                    catch (IOException) { provenanceSaved = false; } catch (UnauthorizedAccessException) { provenanceSaved = false; }
                    await PrepareProjectPlaybackAsync(force: true);
                    RefreshMarkerList(); RefreshWorkspaceDisplay(); RefreshMediaLibrary();
                    PositionSlider.Maximum = Math.Max(0.001, _playback.Duration.TotalSeconds); PositionSlider.Value = 0;
                    PositionSlider.IsEnabled = true; PlayPauseButton.IsEnabled = true; PlayPauseButton.Content = "播放";
                    ClockSourceText.Text = "时钟：WASAPI 设备渲染位置";
                    StatusText.Text = "歌曲已复制到工作区，歌词与已有打轴保持不变。请保存项目。" + (provenanceSaved ? "" : "来源附注未保存，请检查磁盘权限。");
        });
        if (!imported) throw new InvalidOperationException("导入未完成，请检查客户端提示。");
    }
}
