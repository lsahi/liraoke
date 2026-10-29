using System.Windows;
using KaraokeFilmMaker.App.Practice;
using KaraokeFilmMaker.Infrastructure.Audio;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private PracticeRoomWindow? _practiceRoom;
    private bool _openingPracticeRoom;

    private async void PracticeRoom_Click(object sender, RoutedEventArgs e)
    {
        if (_practiceRoom is not null) { _practiceRoom.Activate(); return; }
        if (_isBusy || _openingPracticeRoom) return;
        if (_workspace?.Project.MasterAudioAssetId is null)
        { StatusText.Text = "请先打开项目并导入歌曲，再进入练歌房。"; return; }
        if (!CanLeaveSourceLyricEdit() || !CanLeaveQuickEdit()) return;
        if (_markers.Count > 0 || _undoneMarkers.Count > 0)
        { StatusText.Text = "请先按 Enter 应用当前句的打点，再打开练歌房。"; return; }
        _openingPracticeRoom = true;
        try
        {
            if (_playback.IsPlaying) await _playback.PauseAsync();
            PlayPauseButton.Content = "播放";
            VideoPreview.Pause();
            // Records are replaced by editor actions; the room only reads this snapshot.
            var project = _workspace.Project;
            string root = _workspace.RootPath;
            var room = new PracticeRoomWindow(project, root, _previewPaths, _audioVisualizationData,
                UsePreviewAccompaniment, (accompaniment, cancellation) => new ProjectAudioComposer(_mediaTools)
                    .PrepareAsync(root, project, cancellationToken: cancellation, accompaniment: accompaniment)) { Owner = this };
            _practiceRoom = room;
            room.Closed += (_, _) => { if (ReferenceEquals(_practiceRoom, room)) _practiceRoom = null; };
            room.Show();
            StatusText.Text = "练歌房已打开。AI 助手可继续使用；操作项目时会先关闭练歌房并释放设备。";
        }
        catch (Exception error) { StatusText.Text = "无法打开练歌房：" + error.Message; await LogErrorAsync(error); }
        finally { _openingPracticeRoom = false; }
    }

    private async Task ClosePracticeRoomAsync()
    {
        if (_practiceRoom is { } room)
        { await room.CloseRoomAsync(); if (ReferenceEquals(_practiceRoom, room)) _practiceRoom = null; }
    }
}
