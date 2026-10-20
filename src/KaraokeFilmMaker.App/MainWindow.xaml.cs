using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Japanese;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Audio;
using KaraokeFilmMaker.Infrastructure.Japanese;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Infrastructure.Workspaces;
using KaraokeFilmMaker.App.Controls;
using KaraokeFilmMaker.App.Dialogs;
using Microsoft.Win32;

namespace KaraokeFilmMaker.App;

public partial class MainWindow : Window
{
    private IAudioPlaybackSession _playback = new ScaledAudioPlaybackSession();
    private readonly IWorkspaceService _workspaceService;
    private readonly DialogLocationService _dialogLocations = new();
    private readonly LyricImportService _lyricImportService = new();
    private readonly MeCabJapaneseReadingService _japaneseReadingService = new();
    private readonly JapaneseAnnotationService _japaneseAnnotationService;
    private readonly DispatcherTimer _uiTimer;
    private readonly List<ProjectTime> _markers = [];
    private readonly ObservableCollection<LyricLineEditorRow> _lyricRows = [];
    private WorkspaceSession? _workspace;
    private bool _isClosing;

    public MainWindow() : this(new UserPreferenceStore(AppContext.BaseDirectory)) { }

    public MainWindow(UserPreferenceStore preferences)
    {
        _userPreferenceStore = preferences;
        InitializeComponent();
        _japaneseAnnotationService = new JapaneseAnnotationService(_japaneseReadingService);
        InitializeLanguages();
        var repository = new JsonProjectRepository();
        _workspaceService = new WorkspaceService(repository, new FfprobeMediaReader(_mediaTools));
        LyricsGrid.ItemsSource = _lyricRows;
        VideoGrid.ItemsSource = _videoRows;
        RecordingLineCombo.ItemsSource = _lyricRows;
        LyricsGrid.SelectionChanged += LyricsGrid_SelectionChanged;
        _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1d / 24),
        };
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();
        _recoveryTimer.Tick += RecoveryTimer_Tick;
        _recoveryTimer.Start();
        CurrentRecordingLine.UnitSelected += RecordingUnitSelected;
        NextRecordingLine.UnitSelected += RecordingUnitSelected;
        SongTitleEditor.ApplyRequested += ApplySongTitle;
        RefreshMetronomeFields();
        RefreshWorkAreaFields();
        RefreshAccompanimentState();
        ProjectTimeline.SeekRequested += TimelineSeekRequested;
        ProjectTimeline.EditRequested += TimelineEditRequested;
        ProjectTimeline.ClipSelected += id => VideoGrid.SelectedItem = _videoRows.FirstOrDefault(row => row.Id == id);
        UpdateEditorMode();
    }

    private async void NewWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择一个空文件夹作为工作区",
            Multiselect = false,
        };

        if (!_dialogLocations.ShowDialog(dialog, DialogLocation.NewWorkspace, this))
        {
            return;
        }

        string title = new DirectoryInfo(dialog.FolderName).Name;
        await RunUiOperationAsync(async () =>
        {
            _workspace = await _workspaceService.CreateAsync(dialog.FolderName, title);
            _dialogLocations.SeedOpenWorkspace(_workspace.RootPath);
            ResetMarkers();
            RefreshMarkerList();
            RefreshWorkspaceDisplay();
            RefreshLyricRows();
            RefreshVideoRows();
            RefreshSubtitleStyleFields();
            await ResetPlaybackAsync();
            _previewPaths.Clear(); _thumbnailPaths.Clear();
            RefreshExportPreset();
            await InitializeRecoveryAsync();
            StatusText.Text = "工作区已创建，可以导入歌曲或歌曲视频。";
        });
    }

    private async void OpenWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowOpenWorkspaceDialogAsync();
    }

    private async void SaveWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null)
        {
            return;
        }

        await RunUiOperationAsync(async () =>
        {
            if (!await ApplySourceLyricDraftAsync()) { return; }
            ApplyLyricRowsToProject();
            ApplyVideoRowsToProject();
            ApplySubtitleStyleFields();
            ApplyExportPreset();
            _workspace = await _workspaceService.SaveAsync(_workspace);
            _lastExplicitContent = ProjectRecoveryStore.Content(_workspace.Project);
            _availableRecovery = null;
            RecoveryButton.Visibility = Visibility.Collapsed;
            RefreshWorkspaceDisplay();
            StatusText.Text = _markers.Count > 0
                ? $"项目已保存；另有 {_markers.Count} 个未应用打点仍只在内存中，请完成当前句并按 Enter 应用后再次保存。"
                : "项目已保存。";
        }, allowSourceDraft: true);
    }

    private async void ImportLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "导入歌词到当前工作区",
            Filter = "歌词文件 (*.txt;*.lrc;*.srt)|*.txt;*.lrc;*.srt|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (!_dialogLocations.ShowDialog(dialog, DialogLocation.ImportLyrics, this))
        {
            return;
        }

        await RunUiOperationAsync(async () =>
        {
            LyricDocument lyrics = await _lyricImportService.ImportCopyAsync(
                dialog.FileName,
                _workspace.RootPath,
                GetSelectedLyricLanguage(), _operationCancellation!.Token);
            lyrics = await AnalyzeImportedLyricsAsync(lyrics);
            _workspace = _workspace with
            {
                Project = _workspace.Project with
                {
                    Lyrics = lyrics,
                    ModifiedAtUtc = DateTimeOffset.UtcNow,
                },
            };
            ResetMarkers(); RefreshMarkerList(); RefreshLyricRows();
            StatusText.Text = $"已导入 {lyrics.Lines.Count} 行歌词并离线分析语言 / 读音；请到打轴页检查待确认片段后保存。";
        });
    }

    private async void AnnotateJapaneseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null)
        {
            return;
        }

        if (!CanEditRecordingStructure()) { return; }

        await RunUiOperationAsync(async () =>
        {
            ApplyLyricRowsToProject();
            LyricDocument lyrics = await AnalyzeImportedLyricsAsync(_workspace.Project.Lyrics);
            _workspace = _workspace with
            {
                Project = _workspace.Project with
                {
                    Lyrics = lyrics,
                    ModifiedAtUtc = DateTimeOffset.UtcNow,
                },
            };
            RefreshLyricRows();
            StatusText.Text = "已准备语言 / 读音，人工读音及已打轴单元保留。歧义片段请用“语言 / 片段”确认。";
        });
    }

    private void AddLyricLineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null)
        {
            return;
        }

        var line = new LyricLine(
            Guid.NewGuid(), string.Empty, AnnotationType.None, null, null,
            new TimeRange(ProjectTime.Zero, ProjectTime.Zero), [],
            ReviewState.Unreviewed, null);
        _lyricRows.Add(LyricLineEditorRow.FromLine(_lyricRows.Count + 1, line));
        LyricsGrid.SelectedIndex = _lyricRows.Count - 1;
        LyricsGrid.ScrollIntoView(LyricsGrid.SelectedItem);
        StatusText.Text = "已新增空白歌词行。";
    }

    private void DeleteLyricLineButton_Click(object sender, RoutedEventArgs e)
    {
        if (LyricsGrid.SelectedItem is not LyricLineEditorRow selected)
        {
            return;
        }

        _lyricRows.Remove(selected);
        RenumberLyricRows();
        StatusText.Text = "已删除选中的歌词行。";
    }

    private async void OpenAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "导入歌曲到工作区",
            Filter = "音频文件 (*.mp3;*.wav)|*.mp3;*.wav|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (!_dialogLocations.ShowDialog(dialog, DialogLocation.ImportAudio, this))
        {
            return;
        }

        await RunUiOperationAsync(async () =>
        {
            ApplyLyricRowsToProject();
            ApplyVideoRowsToProject();
            _workspace = await _workspaceService.ImportMediaAsync(
                _workspace,
                dialog.FileName,
                MediaKind.Audio, _operationCancellation!.Token);
            await PrepareProjectPlaybackAsync(force: true);
            ResetMarkers();
            RefreshMarkerList();
            RefreshWorkspaceDisplay();
            PositionSlider.Maximum = Math.Max(0.001, _playback.Duration.TotalSeconds);
            PositionSlider.Value = 0;
            PositionSlider.IsEnabled = true;
            PlayPauseButton.IsEnabled = true;
            PlayPauseButton.Content = "播放";
            ClockSourceText.Text = "时钟：WASAPI 设备渲染位置";
            StatusText.Text = "歌曲已复制到工作区。预览页空格播放 / 暂停；请切换到歌词打轴页记录时间点。";
            RefreshMediaLibrary();
        });
    }

    private async void ImportVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "导入 MP4 视频到工作区",
            Filter = "MP4 视频 (*.mp4)|*.mp4|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (!_dialogLocations.ShowDialog(dialog, DialogLocation.ImportVideo, this))
        {
            return;
        }

        await RunUiOperationAsync(async () =>
        {
            ApplyLyricRowsToProject();
            ApplyVideoRowsToProject();
            Guid? previousMaster = _workspace.Project.MasterAudioAssetId;
            _workspace = await _workspaceService.ImportMediaAsync(
                _workspace,
                dialog.FileName,
                MediaKind.Video, _operationCancellation!.Token);
            var imported = _workspace.Project.VideoClips.Last();
            int track = ProjectTimeline.SelectedTrackIndex;
            ProjectTime trackEnd = _workspace.Project.VideoClips.Where(clip => clip.Id != imported.Id && clip.TrackIndex == track)
                .Select(clip => clip.TimelineEnd).DefaultIfEmpty(ProjectTime.Zero).Max();
            _workspace = _workspace with { Project = _videoTimelineService.UpdateClip(_workspace.Project, imported.Id,
                trackEnd, imported.SourceStart, imported.Duration, imported.IsMuted, track) };
            RefreshWorkspaceDisplay();
            await PreparePreviewsAsync();
            RefreshVideoRows();

            if (previousMaster is null)
            {
                await LoadMasterAudioIfAvailableAsync();
            }

            StatusText.Text = $"视频已复制到工作区并加入 V{track + 1}；可在时间轴上下拖动换轨。";
        });
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        await TogglePlaybackAsync();
    }

    private async Task TogglePlaybackAsync()
    {
        if (!_playback.IsLoaded)
        {
            return;
        }

        await RunUiOperationAsync(async () =>
        {
            if (_playback.IsPlaying)
            {
                await _playback.PauseAsync();
                PlayPauseButton.Content = "播放";
                StatusText.Text = "已暂停。";
            }
            else
            {
                ApplyVideoRowsToProject();
                await PrepareProjectPlaybackAsync();
                await PrepareWorkAreaPlaybackAsync();
                if (_playback.Position.Microseconds >= _playback.Duration.Microseconds)
                {
                    await _playback.SeekAsync(ProjectTime.Zero);
                }

                await _playback.PlayAsync();
                PlayPauseButton.Content = "暂停";
                StatusText.Text = "正在播放并使用 WASAPI 设备时钟。";
            }
        });
    }

    private async void PositionSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_playback.IsLoaded)
        {
            return;
        }

        await RunUiOperationAsync(async () =>
        {
            await _playback.SeekAsync(ProjectTime.FromSeconds(PositionSlider.Value));
            StatusText.Text = $"已跳转到 {FormatTime(_playback.Position)}。";
        });
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (LyricsGrid.IsKeyboardFocusWithin || VideoGrid.IsKeyboardFocusWithin || _isBusy
            || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
            || Keyboard.FocusedElement is Controls.MetronomeValueWheel
            || e.OriginalSource is DependencyObject focused && IsShortcutEditingControl(focused))
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Z or Key.Y)
        {
            e.Handled = true;
            await RestoreHistoryAsync(e.Key == Key.Y);
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && !e.IsRepeat)
        {
            if (e.Key == Key.S) { e.Handled = true; SaveWorkspaceButton_Click(sender, e); return; }
            if (IsPreviewMode && e.Key == Key.B) { e.Handled = true; SplitSelectedClipButton_Click(sender, e); return; }
            if (IsPreviewMode && e.Key == Key.D) { e.Handled = true; DuplicateVideoButton_Click(sender, e); return; }
        }

        if (e.Key == Key.Escape && _playback.IsLoaded && _playback.IsPlaying)
        {
            e.Handled = true;
            await RunUiOperationAsync(async () =>
            {
                await _playback.PauseAsync();
                PlayPauseButton.Content = "播放";
                StatusText.Text = "已通过 Esc 暂停。";
            });
            return;
        }

        if (!_playback.IsLoaded || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        EditorShortcut action = EditorShortcutPolicy.Resolve(e.Key, IsRecordingMode);
        if (action == EditorShortcut.None) { return; }
        e.Handled = true;
        if (e.IsRepeat && action is not (EditorShortcut.PreviousFrame or EditorShortcut.NextFrame)) { return; }
        if (action is EditorShortcut.TogglePlayback or EditorShortcut.Play or EditorShortcut.Pause)
        {
            if (action == EditorShortcut.TogglePlayback || action == EditorShortcut.Play && !_playback.IsPlaying
                || action == EditorShortcut.Pause && _playback.IsPlaying) { await TogglePlaybackAsync(); }
        }
        else if (action is EditorShortcut.PreviousFrame or EditorShortcut.NextFrame or EditorShortcut.SeekBack or EditorShortcut.Start or EditorShortcut.End)
        {
            await RunUiOperationAsync(async () =>
            {
                double step = IsRecordingMode ? 0.1 : 1d / (_workspace?.Project.ExportPreset.FramesPerSecond ?? 30);
                double seconds = Math.Clamp(_playback.Position.TotalSeconds
                    + (action == EditorShortcut.SeekBack ? -1 : action == EditorShortcut.PreviousFrame ? -step : step), 0, _playback.Duration.TotalSeconds);
                if (action == EditorShortcut.Start) { seconds = IsPreviewMode ? _workspace?.Project.GetWorkArea().Start.TotalSeconds ?? 0 : 0; }
                if (action == EditorShortcut.End) { seconds = IsPreviewMode ? _workspace?.Project.GetWorkArea().End.TotalSeconds ?? _playback.Duration.TotalSeconds : _playback.Duration.TotalSeconds; }
                await _playback.SeekAsync(ProjectTime.FromSeconds(seconds));
            });
        }
        else if (action == EditorShortcut.ApplyMarkers)
        {
            e.Handled = true;
            if (TapModeCombo.SelectedIndex == 1)
            {
                ApplyUnitMarkersButton_Click(sender, e);
            }
            else
            {
                ApplyLineMarkersButton_Click(sender, e);
            }
        }
        else if (action == EditorShortcut.Record)
        {
            e.Handled = true;
            if (e.IsRepeat)
            {
                return;
            }

            if (_countdownTarget is { } target && _playback.Position < target)
            {
                StatusText.Text = "倒数准备中，请在目标行开始后记录。";
                return;
            }
            ProjectTime marker = _playback.Position;
            if (!RecordMarker(marker)) { return; }
            RefreshMarkerList();
            StatusText.Text = $"记录 #{_markers.Count}: {FormatTime(marker)}";
        }
        else if (action == EditorShortcut.UndoMarker) { UndoLastMarker(); }
        else if (action == EditorShortcut.DeleteClip && IsPreviewMode) { DeleteVideoButton_Click(sender, e); }
    }

    private static bool IsShortcutEditingControl(DependencyObject focused)
    {
        for (DependencyObject? current = focused; current is not null;)
        {
            if (current is ComboBox or ComboBoxItem or CheckBox or Controls.ColorField or Controls.SongTitleEditor) { return true; }
            current = current is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private void ClearMarkersButton_Click(object sender, RoutedEventArgs e)
    {
        ResetMarkers();
        RefreshMarkerList();
        StatusText.Text = "已清空本次未应用打点；项目中已应用的时间点保留，继续时只录未完成单元。";
    }

    private async void UiTimer_Tick(object? sender, EventArgs e)
    {
        await EnforceWorkAreaPlaybackAsync();
        ProjectTime position = _playback.Position;
        PositionText.Text = FormatTime(position);
        TransportPositionText.Text = FormatTime(position);
        RefreshLyricPreview(position);
        RefreshVideoPreview(position);
        RefreshTapHint(position);
        ProjectTimeline.Update(_workspace?.Project, position);
        if (!_isBusy && _loopRange is { } loop && _playback.IsPlaying && position >= loop.End)
        {
            await RunUiOperationAsync(() => _playback.SeekAsync(loop.Start));
        }

        if (_playback.IsLoaded && !PositionSlider.IsMouseCaptureWithin)
        {
            PositionSlider.Value = Math.Min(position.TotalSeconds, PositionSlider.Maximum);
        }

        if (_playback.IsLoaded && !_playback.IsPlaying && PlayPauseButton.Content?.ToString() == "暂停")
        {
            PlayPauseButton.Content = "播放";
        }
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        _uiTimer.Stop();
        _recoveryTimer.Stop();
        _operationCancellation?.Cancel();
        VideoPreview.Close();
        await _playback.DisposeAsync();
        _japaneseReadingService.Dispose();
        _languagePluginLoader.Dispose();
    }

    private IReadOnlyList<UnitTapTarget>? _listedPlan;
    private bool _markerListDirty = true;

    private void RefreshMarkerList()
    {
        RefreshTapHint(_playback.Position);
        while (MarkerList.Items.Count > _markers.Count) { MarkerList.Items.RemoveAt(MarkerList.Items.Count - 1); }
        int start = !_markerListDirty && ReferenceEquals(_listedPlan, _unitTapTargets) ? MarkerList.Items.Count : 0;
        for (var index = start; index < _markers.Count; index++)
        {
            string label = _unitTapTargets is { } plan
                ? index < plan.Count ? $"行 {plan[index].LineIndex + 1} · 字 {plan[index].UnitIndex + 1}  {plan[index].DisplayText} / {plan[index].Reading}" : "尾字结束"
                : $"行入口 {index + 1}";
            string item = $"{index + 1,3}  {FormatTime(_markers[index])}  {label}";
            if (index >= MarkerList.Items.Count) { MarkerList.Items.Add(item); }
            else if (!Equals(MarkerList.Items[index], item)) { MarkerList.Items[index] = item; }
        }
        _listedPlan = _unitTapTargets; _markerListDirty = false;

        if (MarkerList.Items.Count > 0)
        {
            MarkerList.ScrollIntoView(MarkerList.Items[^1]);
        }
        bool pending = _markers.Count > 0 || _undoneMarkers.Count > 0;
        TapModeCombo.IsEnabled = !pending; RecordingLineCombo.IsEnabled = !pending;
        RefreshTapHint(_playback.Position);
    }

    private async Task LoadMasterAudioIfAvailableAsync()
    {
        if (_workspace?.Project.MasterAudioAssetId is not { } masterId)
        {
            return;
        }

        MediaAsset? asset = _workspace.Project.MediaAssets.FirstOrDefault(item => item.Id == masterId);
        if (asset is null)
        {
            return;
        }

        await PrepareProjectPlaybackAsync(force: true);
        PositionSlider.Maximum = Math.Max(0.001, _playback.Duration.TotalSeconds);
        PositionSlider.Value = 0;
        PositionSlider.IsEnabled = true;
        ClockSourceText.Text = "时钟：WASAPI 设备渲染位置";
    }

    private string GetAssetAbsolutePath(MediaAsset asset)
    {
        if (_workspace is null)
        {
            throw new InvalidOperationException("尚未打开工作区。");
        }

        string root = Path.GetFullPath(_workspace.RootPath);
        string candidate = Path.GetFullPath(Path.Combine(
            root,
            asset.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("项目素材路径超出了工作区范围。");
        }

        return candidate;
    }

    private void RefreshWorkspaceDisplay()
    {
        SongTitleEditor.Load(_workspace?.Project);
        RefreshRoleControls();
        RefreshAccompanimentState();
        RefreshMediaLibrary();
        if (_workspace is null)
        {
            Title = AppBrand.DisplayName;
            ProjectTitleText.Text = AppBrand.DisplayName;
            SourceText.Text = "请先新建或打开工作区";
            return;
        }

        ProjectTitleText.Text = _workspace.Project.Title;
        Title = $"{_workspace.Project.Title} — {AppBrand.DisplayName}";
        SourceText.Text = $"{_workspace.RootPath} · {_workspace.Project.MediaAssets.Count} 个素材";
    }

    private void RefreshLyricRows()
    {
        int selected = Math.Max(0, LyricsGrid.SelectedIndex);
        LyricSourceText.Text = _workspace?.Project.Lyrics.Source is { } source
            ? $"来源：{source.Provider} · {source.Title} — {source.Artist}" : "";
        _lyricRows.Clear();
        if (_workspace is null)
        {
            return;
        }

        LyricLanguageCombo.SelectedIndex = _workspace.Project.Lyrics.Language switch { LyricLanguage.Japanese => 0, LyricLanguage.Chinese => 1, LyricLanguage.English => 2, _ => 3 };
        for (var index = 0; index < _workspace.Project.Lyrics.Lines.Count; index++)
        {
            _lyricRows.Add(LyricLineEditorRow.FromLine(
                index + 1,
                _workspace.Project.Lyrics.Lines[index]));
        }
        if (_lyricRows.Count > 0) { LyricsGrid.SelectedIndex = Math.Min(selected, _lyricRows.Count - 1); }
        RebindRecordingPlan();
        RestoreRecordingProgress();
    }

    private void ApplyLyricRowsToProject()
    {
        if (_workspace is null)
        {
            return;
        }

        if (!LyricsGrid.CommitEdit(DataGridEditingUnit.Cell, true)
            || !LyricsGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            throw new InvalidOperationException("请修正歌词表格中的无效数值。");
        }
        LyricLanguage language = _workspace.Project.Lyrics.Language;
        LyricLine[] lines = _lyricRows.Select(row => row.ToLine(language)).ToArray();
        for (int index = 0; index < lines.Length; index++)
        {
            // Only explicit content edits invalidate the persisted plan; playback/save must not run plugins.
            if (lines[index].LanguageSpans is not null && lines[index].Presentation is null
                && _workspace.Project.Lyrics.Lines.FirstOrDefault(line => line.Id == lines[index].Id)?.Presentation is not null)
            { lines[index] = _languageAnalysis.RefreshPresentation(lines[index]); }
        }
        for (var index = 0; index < lines.Length; index++) { _lyricRows[index].AcceptSource(lines[index]); }
        _workspace = _workspace with
        {
            Project = _workspace.Project with
            {
                Lyrics = new LyricDocument(language, lines, _workspace.Project.Lyrics.Source),
                ModifiedAtUtc = DateTimeOffset.UtcNow,
            },
        };
    }

    private LyricLanguage GetSelectedLyricLanguage() =>
        LyricLanguageCombo.SelectedIndex switch { 0 => LyricLanguage.Japanese, 1 => LyricLanguage.Chinese, 2 => LyricLanguage.English, _ => LyricLanguage.Auto };

    private void RenumberLyricRows()
    {
        LyricLine[] lines = _lyricRows.Select(row => row.ToLine(_workspace?.Project.Lyrics.Language ?? LyricLanguage.Auto)).ToArray();
        _lyricRows.Clear();
        for (var index = 0; index < lines.Length; index++)
        {
            _lyricRows.Add(LyricLineEditorRow.FromLine(index + 1, lines[index]));
        }
    }

    private async Task RunUiOperationAsync(Func<Task> operation, bool allowSourceDraft = false)
    {
        if (_isBusy || (!allowSourceDraft && !CanLeaveSourceLyricEdit()))
        {
            return;
        }

        WorkspaceSession? before = _workspace;
        try
        {
            _isBusy = true;
            RefreshAccompanimentState();
            RefreshWorkAreaAvailability();
            RefreshExportFormatAvailability();
            PlaybackRateCombo.IsEnabled = false;
            _operationCancellation = new CancellationTokenSource();
            CancelOperationButton.IsEnabled = true;
            ExportButton.IsEnabled = false;
            EditorTabs.IsEnabled = false;
            NewWorkspaceButton.IsEnabled = false;
            OpenWorkspaceButton.IsEnabled = false;
            SaveWorkspaceButton.IsEnabled = false;
            OpenAudioButton.IsEnabled = false;
            ImportVideoButton.IsEnabled = false;
            ImportImageButton.IsEnabled = false;
            PlayPauseButton.IsEnabled = false;
            ProjectTimeline.IsEnabled = false;
            ClipToolsPanel.IsEnabled = false;
            await operation();
            await SynchronizeMetronomeAsync();
            if (_workspace is not null) { await PrepareAudioVisualizationAsync(_workspace.Project); }
            if (before is not null && _workspace is not null && before.Project.Id == _workspace.Project.Id
                  && _playback.IsPlaying && (!before.Project.VideoClips.SequenceEqual(_workspace.Project.VideoClips)
                      || !before.Project.Accompaniments.SequenceEqual(_workspace.Project.Accompaniments)))
            {
                string result = StatusText.Text;
                await PrepareProjectPlaybackAsync();
                StatusText.Text = result;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _cancelledExportSummary ?? "操作已取消，不完整输出已清理。";
            _cancelledExportSummary = null;
        }
        catch (Exception exception)
        {
            await LogErrorAsync(exception);
            StatusText.Text = exception.Message;
            MessageBox.Show(this, exception.Message, AppBrand.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // A completed in-memory edit must remain undoable even if a subsequent disk save fails.
            RecordHistory(before);
            _isBusy = false;
            RefreshAccompanimentState();
            if (before?.Project.Id != _workspace?.Project.Id || before?.Project.WorkArea != _workspace?.Project.WorkArea
                || before?.Project.MasterAudioAssetId != _workspace?.Project.MasterAudioAssetId)
            { RefreshWorkAreaFields(); }
            RefreshWorkAreaAvailability();
            RefreshExportFormatAvailability();
            PlaybackRateCombo.IsEnabled = true;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            CancelOperationButton.IsEnabled = false;
            ExportButton.IsEnabled = _workspace is not null;
            EditorTabs.IsEnabled = true;
            NewWorkspaceButton.IsEnabled = true;
            OpenWorkspaceButton.IsEnabled = true;
            SaveWorkspaceButton.IsEnabled = _workspace is not null;
            OpenAudioButton.IsEnabled = _workspace is not null;
            ImportVideoButton.IsEnabled = _workspace is not null;
            ImportImageButton.IsEnabled = _workspace is not null;
            PlayPauseButton.IsEnabled = _playback.IsLoaded;
            ProjectTimeline.IsEnabled = true;
            ClipToolsPanel.IsEnabled = true;
            RefreshAudioVisualizationFields();
            RefreshAudioVisualizationPreview(_playback.Position);
        }
    }

    private static string FormatTime(ProjectTime time)
    {
        TimeSpan value = TimeSpan.FromSeconds(time.TotalSeconds);
        return $"{(int)value.TotalMinutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
    }

    private sealed class LyricLineEditorRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private bool _isSourceEditing;
        private string _sourceEditText = "";
        public bool IsSourceEditing
        {
            get => _isSourceEditing;
            set { _isSourceEditing = value; PropertyChanged?.Invoke(this, new(nameof(IsSourceEditing))); }
        }
        public string SourceEditText
        {
            get => _sourceEditText;
            set { _sourceEditText = value; PropertyChanged?.Invoke(this, new(nameof(SourceEditText))); }
        }
        private LyricLine Source { get; set; } = null!;
        public void AcceptSource(LyricLine line)
        {
            Source = line; ReadingText = line.ReadingText; RomanizationText = line.RomanizationText;
            IsReadingConfirmed = line.ReadingSource == KaraokeFilmMaker.Domain.Lyrics.ReadingSource.Confirmed;
        }

        public required int Number { get; init; }
        public Guid LineId => Source.Id;
        public ReviewState ReviewState { get; set; }

        public required string PrimaryText { get; set; }

        public string? ReadingText { get; set; }

        public string? RomanizationText { get; set; }

        public string? TranslationText { get; set; }

        public AnnotationType AnnotationType { get; set; }

        public double StartSeconds { get; set; }

        public double EndSeconds { get; set; }

        // Presentation-only rounding: keep full precision for save, retiming and export.
        public string StartDisplay => Math.Round(StartSeconds, 1, MidpointRounding.AwayFromZero).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);
        public string EndDisplay => Math.Round(EndSeconds, 1, MidpointRounding.AwayFromZero).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);

        public bool IsReadingConfirmed { get; set; }

        public bool IsHighlightEnabled { get; set; }

        public bool IsInstrumental { get; set; }

        public static LyricLineEditorRow FromLine(int number, LyricLine line) => new()
        {
            Source = line,
            Number = number,
            ReviewState = line.ReviewState,
            PrimaryText = line.PrimaryText,
            ReadingText = line.ReadingText ?? line.AnnotationText,
            RomanizationText = line.RomanizationText,
            TranslationText = line.TranslationText,
            AnnotationType = line.AnnotationType,
            StartSeconds = line.Timing.Start.TotalSeconds,
            EndSeconds = line.Timing.End.TotalSeconds,
            IsReadingConfirmed = line.ReadingSource == KaraokeFilmMaker.Domain.Lyrics.ReadingSource.Confirmed,
            IsHighlightEnabled = line.IsHighlightEnabled,
            IsInstrumental = line.IsInstrumental,
        };

        public LyricLine ToLine(LyricLanguage language)
        {
            bool readingChanged = !string.Equals(
                ReadingText,
                Source.ReadingText ?? Source.AnnotationText,
                StringComparison.Ordinal);
            bool romanizationChanged = !string.Equals(
                RomanizationText,
                Source.RomanizationText,
                StringComparison.Ordinal);
            string[]? spanLanguages = Source.LanguageSpans?.Select(span => span.LanguageId).Distinct().ToArray();
            if ((Source.LanguageSpans is { Count: > 1 } && readingChanged)
                || (spanLanguages is { Length: > 1 } && romanizationChanged))
            { throw new InvalidOperationException("多片段歌词的读音请在“语言 / 片段”或“单元精修”中修改，避免覆盖已指定的片段边界。"); }
            if (spanLanguages is { Length: 1 }) { language = LanguageIds.ToLegacy(spanLanguages[0]); }
            LyricLine source = Source;
            bool textChanged = !string.Equals(PrimaryText, Source.PrimaryText, StringComparison.Ordinal);
            if (textChanged)
            {
                LyricDocument parsed = new LyricImportService().Parse(PrimaryText, ".txt", language);
                source = source with { Units = parsed.Lines.FirstOrDefault()?.Units ?? [], LanguageSpans = null, Presentation = null };
                // Rebuild mappings after text edits; old ranges may no longer address the same characters.
                source = source with { Timing = new TimeRange(ProjectTime.Zero, ProjectTime.Zero) };
            }

            var timing = new TimeRange(
                ProjectTime.FromSeconds(StartSeconds), ProjectTime.FromSeconds(EndSeconds));
            if (source.Timing != timing)
            {
                source = LyricTimingService.Retiming(source, timing);
            }
            source = source with
            {
                PrimaryText = PrimaryText,
                AnnotationType = AnnotationType,
                AnnotationText = ReadingText,
                ReadingText = ReadingText,
                RomanizationText = RomanizationText,
                TranslationText = TranslationText,
                IsInstrumental = IsInstrumental,
                IsHighlightEnabled = IsHighlightEnabled,
                ReviewState = ReviewState,
                ReadingSource = textChanged ? null : IsReadingConfirmed
                    ? KaraokeFilmMaker.Domain.Lyrics.ReadingSource.Confirmed
                    : readingChanged || romanizationChanged
                    ? KaraokeFilmMaker.Domain.Lyrics.ReadingSource.Manual
                    : Source.ReadingSource == KaraokeFilmMaker.Domain.Lyrics.ReadingSource.Confirmed
                        ? KaraokeFilmMaker.Domain.Lyrics.ReadingSource.Generated
                        : Source.ReadingSource,
            };
            if (textChanged && !readingChanged)
            {
                source = source with { ReadingText = null, AnnotationText = null, RomanizationText = null };
            }
            if (language == LyricLanguage.Japanese && readingChanged && !string.IsNullOrWhiteSpace(ReadingText))
            {
                source = JapaneseAnnotationService.ApplyManualReading(source, ReadingText);
            }
            if (language == LyricLanguage.Japanese && romanizationChanged && !string.IsNullOrWhiteSpace(RomanizationText))
            {
                string[] parts = RomanizationText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != source.Units.Count)
                { throw new InvalidOperationException("手动修改整句罗马音时，请用空格分隔每个演唱单元，或在演唱单元编辑器中逐个修改。"); }
                source = source with { Units = source.Units.Select((unit, index) => unit with { Romanization = parts[index] }).ToArray(),
                    RomanizationText = RomanizationText };
            }
            if (readingChanged || romanizationChanged) { source = source with { Presentation = null }; }
            return source;
        }
    }
}
