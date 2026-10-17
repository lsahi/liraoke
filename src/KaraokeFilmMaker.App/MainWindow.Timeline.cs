using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Application.Timeline;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;
using KaraokeFilmMaker.Domain.Validation;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private readonly ObservableCollection<VideoClipEditorRow> _videoRows = [];
    private readonly VideoTimelineService _videoTimelineService = new();
    private readonly LyricTimingService _lyricTimingService = new();
    private bool _isBusy;

    private void RefreshVideoRows()
    {
        Guid? selected = (VideoGrid.SelectedItem as VideoClipEditorRow)?.Id ?? ProjectTimeline.SelectedClipId;
        _videoRows.Clear();
        if (_workspace is null)
        {
            return;
        }

        foreach (VideoClip clip in _workspace.Project.VideoClips.OrderBy(item => item.TimelineStart))
        {
            string name = _workspace.Project.MediaAssets.FirstOrDefault(item => item.Id == clip.MediaAssetId)?.DisplayName
                ?? "缺失素材";
            _videoRows.Add(new VideoClipEditorRow
            {
                Id = clip.Id,
                MediaAssetId = clip.MediaAssetId,
                HasSourceTiming = _workspace.Project.MediaAssets.FirstOrDefault(item => item.Id == clip.MediaAssetId)?.Kind != KaraokeFilmMaker.Domain.Media.MediaKind.Image,
                Name = name,
                ThumbnailPath = _thumbnailPaths.GetValueOrDefault(clip.MediaAssetId),
                TimelineStartSeconds = clip.TimelineStart.TotalSeconds,
                SourceStartSeconds = clip.SourceStart.TotalSeconds,
                DurationSeconds = clip.Duration.TotalSeconds,
                IsMuted = clip.IsMuted,
                TrackNumber = clip.TrackIndex + 1,
            });
        }
        VideoGrid.SelectedItem = _videoRows.FirstOrDefault(row => row.Id == selected);
        RefreshMediaLibrary();
    }

    private void ApplyVideoRowsToProject()
    {
        if (_workspace is null)
        {
            return;
        }

        if (!VideoGrid.CommitEdit(DataGridEditingUnit.Cell, true)
            || !VideoGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            throw new InvalidOperationException("请修正视频表格中的无效数值。");
        }
        var project = _workspace.Project with
        {
            VideoClips = _videoRows.Select(row => row.ToClip()).OrderBy(clip => clip.TimelineStart).ToArray(),
            ModifiedAtUtc = DateTimeOffset.UtcNow,
        };
        IReadOnlyList<string> errors = ProjectValidator.Validate(project);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        _workspace = _workspace with { Project = project };
    }

    private async void ApplyVideoEditsButton_Click(object sender, RoutedEventArgs e) =>
        await RunUiOperationAsync(() =>
        {
            ApplyLyricRowsToProject();
            ApplyVideoRowsToProject();
            RefreshVideoRows();
            StatusText.Text = "视频片段修改已应用；保存项目可持久化这些修改。";
            return Task.CompletedTask;
        });

    private async void DuplicateVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || VideoGrid.SelectedItem is not VideoClipEditorRow row)
        {
            return;
        }

        await RunUiOperationAsync(() =>
        {
            ApplyVideoRowsToProject();
            _workspace = _workspace with { Project = _videoTimelineService.DuplicateClip(_workspace.Project, row.Id) };
            RefreshVideoRows();
            StatusText.Text = "已复制片段并追加到时间轴末尾。";
            return Task.CompletedTask;
        });
    }

    private async void DeleteVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is not null && VideoGrid.SelectedItem is VideoClipEditorRow row)
        {
            await RunUiOperationAsync(() =>
            {
                ApplyVideoRowsToProject();
                _workspace = _workspace with { Project = _videoTimelineService.RemoveClip(_workspace.Project, row.Id) };
                RefreshVideoRows();
                StatusText.Text = "已从时间轴删除片段；工作区素材仍保留，可撤销。";
                return Task.CompletedTask;
            });
        }
    }

    private void MoveVideoUpButton_Click(object sender, RoutedEventArgs e) => MoveVideoRow(-1);

    private void MoveVideoDownButton_Click(object sender, RoutedEventArgs e) => MoveVideoRow(1);

    private void MoveVideoRow(int offset)
    {
        int index = VideoGrid.SelectedIndex;
        int target = index + offset;
        if (index >= 0 && target >= 0 && target < _videoRows.Count)
        {
            VideoGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            VideoGrid.CommitEdit(DataGridEditingUnit.Row, true);
            _videoRows.Move(index, target);
            VideoGrid.SelectedIndex = target;
        }
    }

    private async void JoinVideosButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null)
        {
            return;
        }

        await RunUiOperationAsync(() =>
        {
            VideoGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            VideoGrid.CommitEdit(DataGridEditingUnit.Row, true);
            ProjectTime cursor = ProjectTime.Zero;
            // Join before validating overlap so reordering and crop changes can be applied together.
            foreach (VideoClipEditorRow row in _videoRows)
            {
                row.TrackNumber = 1;
                row.TimelineStartSeconds = cursor.TotalSeconds;
                cursor += ProjectDuration.FromSeconds(row.DurationSeconds);
            }

            ApplyVideoRowsToProject();
            RefreshVideoRows();
            StatusText.Text = "已按列表顺序无间隙拼接视频片段。";
            return Task.CompletedTask;
        });
    }

    private async void ApplyLineMarkersButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || !_playback.IsLoaded)
        {
            return;
        }

        int firstLine = Math.Max(0, LyricsGrid.SelectedIndex);
        await RunUiOperationAsync(() =>
        {
            ApplyLyricRowsToProject();
            LyricDocument lyrics = _lyricTimingService.ApplyLineMarkers(
                _workspace.Project.Lyrics, _markers,
                ProjectTime.Zero + _playback.Duration, firstLine);
            _workspace = _workspace with
            {
                Project = _workspace.Project with { Lyrics = lyrics, ModifiedAtUtc = DateTimeOffset.UtcNow },
            };
            RefreshLyricRows();
            LyricsGrid.SelectedIndex = firstLine;
            StatusText.Text = $"已从第 {firstLine + 1} 行应用 {_markers.Count} 个入口时间；可以在打轴页编辑整句的起止时间。";
            return Task.CompletedTask;
        });
    }

    private void RefreshLyricPreview(ProjectTime position)
    {
        if (!IsPreviewMode) { return; }
        RefreshAudioVisualizationPreview(position);
        SubtitlePreview.Update(_workspace?.Project, position);
    }

    private async void ApplyLyricsButton_Click(object sender, RoutedEventArgs e) =>
        await RunUiOperationAsync(() =>
        {
            ApplyLyricRowsToProject();
            RefreshLyricRows();
            StatusText.Text = "歌词与行时间已应用。";
            return Task.CompletedTask;
        });

    private async void EditLyricUnitsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || LyricsGrid.SelectedIndex < 0)
        {
            return;
        }

        int index = LyricsGrid.SelectedIndex;
        await RunUiOperationAsync(() =>
        {
            ApplyLyricRowsToProject();
            LyricLine[] lines = _workspace.Project.Lyrics.Lines.ToArray();
            var editor = new LyricUnitEditorWindow(lines[index], _workspace.Project.Lyrics.Language) { Owner = this };
            if (editor.ShowDialog() == true && editor.EditedLine is { } edited)
            {
                lines[index] = _languageAnalysis.RefreshPresentation(edited);
                _workspace = _workspace with
                {
                    Project = _workspace.Project with
                    {
                        Lyrics = _workspace.Project.Lyrics with { Lines = lines },
                        ModifiedAtUtc = DateTimeOffset.UtcNow,
                    },
                };
                RefreshLyricRows();
                LyricsGrid.SelectedIndex = index;
                StatusText.Text = "演唱单元与读音修改已应用。";
            }

            return Task.CompletedTask;
        });
    }

    private async void ApplyUnitMarkersButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || LyricsGrid.SelectedIndex < 0)
        {
            StatusText.Text = "请先选中起始歌词行，再按空格逐字记录。";
            return;
        }

        if (_unitTapFirstLine is not { } firstLine || _unitTapTargets is null || _markers.Count == 0)
        { StatusText.Text = "尚未记录字的入口；一次空格对应一个演唱单元。"; return; }
        if (!TryCorrectTapTime(_playback.Position, out ProjectTime end, isEndMarker: true)) { return; }
        await RunUiOperationAsync(() =>
        {
            ApplyLyricRowsToProject();
            var currentPlan = UnitTapPlan.Build(_workspace.Project.Lyrics, firstLine, onlyUnconfirmed: !_rerecordFromSelection);
            if (!_unitTapTargets.SequenceEqual(currentPlan))
            { throw new InvalidOperationException("录制期间歌词或演唱单元已变化。请清空本次时间点后重新录制，避免将时间套到错误的字上。"); }
            int count = Math.Min(_markers.Count, _unitTapTargets.Count);
            LyricDocument lyrics = _lyricTimingService.ApplyContinuousUnitMarkers(_workspace.Project.Lyrics, _markers,
                ProjectTime.Zero + _playback.Duration, firstLine, end, _rawMarkers, onlyUnconfirmed: !_rerecordFromSelection);
            _workspace = _workspace with
            {
                Project = _workspace.Project with
                {
                    Lyrics = lyrics,
                    ModifiedAtUtc = DateTimeOffset.UtcNow,
                },
            };
            ResetMarkers(); RefreshMarkerList(); RefreshLyricRows();
            StatusText.Text = $"已应用 {count} 个演唱单元的真实入口及尾字结束时间；无需先逐行打轴，原始敲击时间已保留。";
            return Task.CompletedTask;
        });
    }

    private sealed class VideoClipEditorRow
    {
        public Guid Id { get; init; }
        public Guid MediaAssetId { get; init; }
        public required string Name { get; init; }
        public string? ThumbnailPath { get; init; }
        public bool HasSourceTiming { get; init; } = true;
        public double TimelineStartSeconds { get; set; }
        public double SourceStartSeconds { get; set; }
        public double DurationSeconds { get; set; }
        public bool IsMuted { get; set; }
        public int TrackNumber { get; set; } = 1;

        public VideoClip ToClip() => new(
            Id, MediaAssetId, ProjectTime.FromSeconds(TimelineStartSeconds),
            ProjectTime.FromSeconds(SourceStartSeconds),
            ProjectDuration.FromSeconds(DurationSeconds), IsMuted, TrackNumber - 1);
    }
}
