using System.IO;
using System.Text.Json;
using System.Windows;
using KaraokeFilmMaker.Application.Music;
using KaraokeFilmMaker.Infrastructure.Music;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.App.Dialogs;
using Microsoft.Win32;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private readonly Dictionary<string, PreparedMusicAudio> _aiSongs = [];
    private bool _aiActionPending;
    private async Task<object> ExecuteAiToolAsync(Guid? projectId, string name, JsonElement args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CheckAiProject(projectId);
        if(name is "get_user_memory" or "remember_user_preference" or "forget_user_preference")return await ExecuteMemoryToolAsync(name,args,token);
        if (name is "get_alignment_plugins" or "get_alignment_install_guide" or "open_alignment_plugins" or "install_alignment_plugin" or "align_lyrics") return await ExecuteAlignmentToolAsync(name, args, token);
        if (name == "open_asr_configuration") return new { configured = ShowAsrConfiguration() };
        if (name == "correct_japanese_readings") return await StartReadingCorrectionAsync(true, token);
        if(name is "import_lyrics" or "edit_lyrics") return await ExecuteAiLyricsAsync(name,args,token);
        bool edit = name is "update_subtitle_style" or "update_song_title" or "set_visualization" or "import_music" or "create_workspace";
        if (!edit && name != "seek_preview") return await ExecuteAiToolCoreAsync(projectId, name, args, token);
        if (_aiActionPending) throw new InvalidOperationException("请先完成当前待批准操作。");
        _aiActionPending = true;
        try
        {
            CheckAiDrafts();
            string? folder = null;
            string title = TextArg(args, "title")?.Trim() ?? "";
            if (name == "create_workspace")
            {
                if (title.Length is 0 or > 100 || title.Any(char.IsControl)) throw new InvalidOperationException("工作区名称需要 1–100 个字符。");
                var picker = new OpenFolderDialog { Title = "为 AI 创建的工作区选择空文件夹", Multiselect = false };
                if (!_dialogLocations.ShowDialog(picker, DialogLocation.NewWorkspace, this)) return new { approved = false, executed = false };
                folder = picker.FolderName;
                if (Directory.EnumerateFileSystemEntries(folder).Any()) throw new InvalidOperationException("新工作区必须使用空文件夹。");
            }
            PreparedMusicAudio? song = null;
            if (name == "import_music" && (!_aiSongs.TryGetValue(TextArg(args,"selectionId") ?? "", out song) || !song.CanImport))
                throw new InvalidOperationException("歌曲卡片已失效或仅能试听，请重新搜索完整歌曲。");
            if (_workspace is null && name != "create_workspace") throw new InvalidOperationException("请先创建或打开工作区。");
            string? expected = _workspace is null ? null : ProjectContent(_workspace.Project);
            string label = name switch { "create_workspace" => "创建工作区", "update_subtitle_style" => "修改字幕样式", "update_song_title" => "修改曲名", "set_visualization" => "修改可视化", "import_music" => "导入歌曲", _ => "跳转预览" };
            string detail = name == "create_workspace" ? $"{title}\n{folder}\n当前项目（如有）会先保存，然后创建新工作区。"
                : song is not null ? $"{song.Track.Title} · {song.Track.Artist}\n替换当前项目歌曲；歌词与已打轴时间保持不变。"
                : DescribeAiChange(args);
            if (edit) detail += "\n批准后自动保存整个项目；已有未保存修改也会一并保存。";
            if (!await AssistantPanel.RequestApprovalAsync(label, detail, token)) return new { approved = false, executed = false };
            token.ThrowIfCancellationRequested(); CheckAiProject(projectId); CheckAiDrafts();
            if (expected != (_workspace is null ? null : ProjectContent(_workspace.Project)))
                throw new InvalidOperationException("等待批准期间项目已修改，请重新发起操作。");
            if (name == "create_workspace")
            {
                var created = await CreateAiWorkspaceAsync(folder!, title, token);
                AssistantPanel.ReportActionResult("工作区已创建并保存。"); return created;
            }
            object result;
            if (song is not null)
            {
                if (!_aiSongs.Values.Contains(song)) throw new InvalidOperationException("歌曲卡片已失效。");
                AssistantPanel.StopAudition();
                await ImportPreparedMusicAsync(song, token);
                result = new { song.Track.Title, song.Track.Artist, imported = true };
            }
            else result = await ExecuteAiToolCoreAsync(projectId, name, args, token);
            if (edit)
            {
                // Once committed, finish its save even if the chat is stopped during disk I/O.
                bool saved = false;
                await RunAiOperationAsync(async () => {
                    try { await SaveAiWorkspaceAsync(); saved = true; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { StatusText.Text = "AI 修改已应用，但自动保存失败，请手动保存。"; }
                });
                if (!saved) { AssistantPanel.ReportActionResult("已应用，但自动保存失败；请手动保存。"); return new { approved = true, executed = true, saved = false, error = "修改已应用，但自动保存失败；请手动保存。", result }; }
            }
            AssistantPanel.ReportActionResult(edit ? "已执行并保存，可撤销。" : "已跳转预览。");
            return new { approved = true, executed = true, saved = edit, result };
        }
        catch { AssistantPanel.ReportActionResult("操作未完成，请查看回复或客户端提示。"); throw; }
        finally { _aiActionPending = false; }
    }
    private string DescribeAiChange(JsonElement args)
    {
        var fields = args.TryGetProperty("patch", out var patch) ? patch : args;
        var labels = new Dictionary<string, string> {
            ["fontFamily"]="字体", ["primaryFontSize"]="主字幕字号", ["annotationFontSize"]="注音字号",
            ["inactiveColor"]="未唱颜色", ["activeColor"]="已唱颜色", ["outlineColor"]="描边颜色", ["activeOutlineColor"]="已唱描边颜色",
            ["outlineWidth"]="描边宽度", ["shadowOffset"]="阴影偏移", ["lineGap"]="行间距", ["marginRatio"]="边距比例",
            ["showRomanization"]="显示罗马音", ["text"]="曲名", ["fontSize"]="字号", ["color"]="颜色", ["position"]="位置",
            ["enabled"]="启用", ["mode"]="可视化模式", ["seconds"]="跳转时间（秒）", ["line"]="跳转歌词行" };
        string Value(JsonElement value) => value.ToString() switch {
            "True" or "true" => "是", "False" or "false" => "否", "TopLeft" => "左上", "TopRight" => "右上",
            "Classic" => "经典频谱", "BottomDense" => "底部密集频谱", "CenterWhite" => "居中白色频谱", "RotatingRing" => "旋转环形频谱", var text => text };
        string detail = string.Join("\n", fields.EnumerateObject().Where(p => p.Name != "roleId").Select(p => $"{labels.GetValueOrDefault(p.Name, p.Name)}：{Value(p.Value)}"));
        if (args.TryGetProperty("roleId", out var role))
        {
            var target = _workspace?.Project.Roles.FirstOrDefault(r => r.Id.ToString() == role.GetString());
            detail = $"角色：{target?.Name ?? "未知角色"}\n" + detail;
        }
        return detail;
    }
    private void CheckAiProject(Guid? id)
    {
        CheckAiProjectIdentity(id);
        if (_isBusy) throw new InvalidOperationException("客户端正在执行其他任务，请稍后重试。");
    }
    private void CheckAiProjectIdentity(Guid? id)
    {
        if (_isClosing || _closingRequested || _workspace?.Project.Id != id)
            throw new InvalidOperationException("项目已切换或窗口正在关闭，请重新发起请求。");
    }
    private void CheckAiDrafts()
    {
        if (!CanLeaveSourceLyricEdit() || !CanLeaveQuickEdit()) throw new InvalidOperationException("请先处理当前歌词编辑草稿。");
        if (_markers.Count > 0 || _undoneMarkers.Count > 0) throw new InvalidOperationException("请先应用或清除本次打点。");
    }
    private async Task SaveAiWorkspaceAsync()
    {
        if (_workspace is null) return;
        // Match explicit Save: collect already approved editor fields before persisting.
        // Unapplied lyric text / quick-edit drafts and timing are rejected by CheckAiDrafts.
        ApplyLyricRowsToProject(); ApplyVideoRowsToProject(); ApplySubtitleStyleFields(); ApplyExportPreset();
        _workspace = await _workspaceService.SaveAsync(_workspace);
        _lastExplicitContent = ProjectRecoveryStore.Content(_workspace.Project);
        _availableRecovery = null; RecoveryButton.Visibility = Visibility.Collapsed;
        RefreshWorkspaceDisplay(); StatusText.Text = "AI 操作已应用并保存。";
    }
    private async Task<object> CreateAiWorkspaceAsync(string folder, string title, CancellationToken token)
    {
        bool created = false;
        await RunAiOperationAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            if (_workspace is not null) await SaveAiWorkspaceAsync();
            var session = await _workspaceService.CreateAsync(folder, title, token);
            _workspace = session;
            _dialogLocations.SeedOpenWorkspace(session.RootPath);
            ResetMarkers(); RefreshMarkerList(); RefreshWorkspaceDisplay(); RefreshLyricRows(); RefreshVideoRows(); RefreshSubtitleStyleFields();
            AssistantPanel.StopAudition(); await ResetPlaybackAsync();
            _previewPaths.Clear(); _thumbnailPaths.Clear(); RefreshMediaLibrary(); RefreshExportPreset(); await InitializeRecoveryAsync();
            created = true; StatusText.Text = "AI 已创建并保存工作区。";
        });
        if (!created) throw new InvalidOperationException("创建工作区未完成，请检查客户端提示。");
        return new { created = true, saved = true, _workspace!.Project.Id, _workspace.Project.Title };
    }
    private async Task<object> SearchInlineMusicAsync(JsonElement args, CancellationToken token)
    {
        var progress = AssistantPanel.CreateToolProgress("正在搜索并检查可播放歌曲…");
        var host = new MusicHostClient();
        var registry = new MusicSourceRegistry([new NeteaseMusicSourceAdapter(host), new TencentMusicSourceAdapter(host), new QishuiMusicSourceAdapter(host)]);
        var accounts = _userPreferenceStore.Load().MusicAccounts;
        MusicSourceCredentials Credentials(string id) => UserPreferenceStore.ReadMusicAccount(accounts.FirstOrDefault(a => a.SourceId == id));
        var result = await registry.SearchAsync(TextArg(args, "query") ?? "", TextArg(args, "source"), Credentials, token);
        using var client = PublicMediaHttp.CreateClient();
        var transfer = new MusicAudioTransfer(_mediaTools, host, client);
        // Interleave providers by their own result rank so an unavailable source cannot consume all probes.
        var candidates = result.Tracks.GroupBy(t => t.SourceId).SelectMany(g => g.Select((track, rank) => new { track, rank })).OrderBy(x => x.rank).Select(x => x.track);
        var found = await FirstPlayableMusic.FindAsync(candidates, (track, ct) => {
            progress.Report($"正在检查：{track.Title} · {track.SourceLabel}");
            return transfer.PrepareAsync(registry.Get(track.SourceId), track, Credentials(track.SourceId), cancellationToken: ct);
        }, token);
        progress.Report(found.Audio is null ? "本次候选中未找到可播放音频。" : "已找到可播放歌曲，点击卡片试听。");
        if (found.Audio is not { } audio) return new { playable = false, checkedCount = found.CheckedCount, message = "本次前 10 个候选未找到可播放音频；可指定音源或更换关键词。", result.Warnings };
        if (token.IsCancellationRequested || _isClosing) { audio.Dispose(); token.ThrowIfCancellationRequested(); throw new OperationCanceledException(); }
        string id = Guid.NewGuid().ToString("N"); _aiSongs.Add(id, audio);
        AssistantPanel.ShowMusicCard(audio, async () => {
            if (_isBusy) throw new InvalidOperationException("请等待当前任务完成。");
            await _playback.PauseAsync(); VideoPreview.Pause(); PlayPauseButton.Content = "播放";
        }, async () => {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { selectionId = id }));
            await ExecuteAiToolAsync(_workspace?.Project.Id, "import_music", doc.RootElement, CancellationToken.None);
        }, () => _aiSongs.Remove(id));
        return new { playable = true, selectionId = id, audio.Track.Title, audio.Track.Artist, audio.Track.SourceId,
            audio.DurationSeconds, audio.CanImport, audio.Availability, checkedCount = found.CheckedCount, result.Warnings };
    }
}
