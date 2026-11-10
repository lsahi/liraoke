using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows.Controls;
using KaraokeFilmMaker.Application.Music;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Validation;
using KaraokeFilmMaker.Infrastructure.Music;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Infrastructure.Rendering;

namespace KaraokeFilmMaker.App;
public partial class MainWindow
{
    private static readonly JsonSerializerOptions ToolJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string? TextArg(JsonElement args, string name) => args.TryGetProperty(name, out var value) ? value.GetString() : null;
    private static T PatchTool<T>(T current, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("patch 必须是对象。");
        var node = JsonSerializer.SerializeToNode(current, ToolJson)!.AsObject();
        foreach (var item in patch.EnumerateObject())
        {
            if (!node.ContainsKey(item.Name)) throw new InvalidOperationException("未知样式字段：" + item.Name);
            node[item.Name] = JsonNode.Parse(item.Value.GetRawText());
        }
        return node.Deserialize<T>(ToolJson)!;
    }
    private async Task<object> ExecuteAiToolCoreAsync(Guid? projectId, string name, JsonElement args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_workspace?.Project.Id != projectId) throw new InvalidOperationException("项目已切换，请重新发起请求。");
        if (_isBusy) throw new InvalidOperationException("客户端正在执行其他任务，请稍后重试。");
        if (name is "search_music" or "open_music_search") return await SearchInlineMusicAsync(args, token);
        if (name == "get_project_context" && _workspace is null) return new { hasWorkspace = false };
        if (_workspace is null) throw new InvalidOperationException("请先创建或打开工作区。");
        var project = _workspace.Project;
        if (name == "get_project_context") return new { project.Id, project.Title, project.ExportPreset,
            song = project.MediaAssets.Where(a=>a.Id==project.MasterAudioAssetId).Select(a=>new{a.Id,a.DisplayName,seconds=a.Duration.TotalSeconds}),
            page = (EditorTabs.SelectedItem as TabItem)?.Header?.ToString(), selectedLyricIndex = LyricsGrid.SelectedIndex + 1, selectedVideoIndex = VideoGrid.SelectedIndex + 1, selectedMediaIndex = MediaLibraryList.SelectedIndex + 1, selectedRoleId = (ProjectRolesList.SelectedItem as SingingRole)?.Id,
            media = project.MediaAssets.Select(a => new { a.Id, a.DisplayName, a.Kind }),
            positionSeconds = _playback.Position.TotalSeconds, project.SongTitle, project.AudioVisualizationEnabled, project.AudioVisualizationStyle,
            pendingTiming = _markers.Count + _undoneMarkers.Count };
        if (name == "get_lyrics")
        {
            int start = args.TryGetProperty("startLine",out var a)?a.GetInt32():1, count=args.TryGetProperty("count",out var b)?b.GetInt32():20;
            if(start<1||count<1||count>50)throw new InvalidOperationException("行号从1开始，每次最多50行。");
            return new { total=project.Lyrics.Lines.Count, startLine=start, lines=project.Lyrics.Lines.Skip(start-1).Take(count).ToArray() };
        }
        Guid? roleId = TextArg(args,"roleId") is { } role ? Guid.Parse(role) : null;
        var selectedRole = roleId is null ? null : project.Roles.SingleOrDefault(r=>r.Id==roleId) ?? throw new InvalidOperationException("找不到指定角色。");
        if(name=="get_subtitle_style")return selectedRole?.Style??project.SubtitleStyle;
        if(name=="list_style_resources")return new { fonts=SystemFontCatalog.Names.Where(x=>x.Contains(TextArg(args,"fontQuery")??"",StringComparison.OrdinalIgnoreCase)).Take(200).ToArray(),
            fontLimit=200, presets=SubtitleStylePresets.All, savedStyles=_userPreferenceStore.Load().SubtitleStyles, roles=project.Roles };
        if (!CanLeaveSourceLyricEdit() || !CanLeaveQuickEdit()) throw new InvalidOperationException("请先处理当前歌词编辑草稿。");
        if (_markers.Count>0||_undoneMarkers.Count>0) throw new InvalidOperationException("请先应用或清除本次打点。");
        token.ThrowIfCancellationRequested();
        if(name=="seek_preview")
        {
            if(args.TryGetProperty("seconds",out var seconds)==args.TryGetProperty("line",out var line))throw new InvalidOperationException("seconds 与 line 必须且只能提供一个。");
            double target;
            if(line.ValueKind!=JsonValueKind.Undefined){int index=line.GetInt32()-1;if(index<0||index>=project.Lyrics.Lines.Count)throw new InvalidOperationException("歌词行不存在。");target=project.Lyrics.Lines[index].Timing.Start.TotalSeconds;}
            else target=seconds.GetDouble();
            if(!_playback.IsLoaded||!double.IsFinite(target)||target<0||target>_playback.Duration.TotalSeconds)throw new InvalidOperationException("没有可播放歌曲，或时间超出范围。");
            await _playback.PauseAsync();token.ThrowIfCancellationRequested();await _playback.SeekAsync(ProjectTime.FromSeconds(target));
            VideoPreview.Pause();PlayPauseButton.Content="播放";PositionSlider.Value=target;RefreshLyricPreview(_playback.Position);
            return new { positionSeconds=_playback.Position.TotalSeconds };
        }
        if(name=="set_visualization")
        {
            var style = args.TryGetProperty("mode",out var m)?Enum.Parse<AudioVisualizationStyle>(m.GetString()!):project.AudioVisualizationStyle;
            bool enabled = args.TryGetProperty("enabled",out var en)?en.GetBoolean():project.AudioVisualizationEnabled;
            if(!style.IsAvailable())throw new InvalidOperationException("不支持此可视化模式。");
            await RunUiOperationAsync(async () =>
            {
                var operation = _operationCancellation!;
                using var linked = token.Register(() => operation.Cancel());
                var visualProject = project with { AudioVisualizationStyle = style, AudioVisualizationEnabled = enabled };
                if(enabled) await PrepareAudioVisualizationAsync(visualProject, explicitRequest: true);
                token.ThrowIfCancellationRequested();
                _workspace = _workspace with { Project = visualProject with { ModifiedAtUtc = DateTimeOffset.UtcNow } };
                if(!enabled) ClearAudioVisualization();
                RefreshAudioVisualizationFields(); RefreshAudioVisualizationPreview(_playback.Position);
            });
            token.ThrowIfCancellationRequested();
            if(_workspace.Project.AudioVisualizationStyle!=style||_workspace.Project.AudioVisualizationEnabled!=enabled)throw new InvalidOperationException("可视化未完整应用，请检查客户端状态。");
            return new { enabled, mode=style.ToString() };
        }
        var candidate=project;
        if(name=="update_subtitle_style")
        {
            var style=PatchTool(selectedRole?.Style??project.SubtitleStyle,args.GetProperty("patch"));
            if(!ProjectValidator.IsValidStyle(style))throw new InvalidOperationException("字幕样式参数无效。");
            candidate=selectedRole is null?project with{SubtitleStyle=style}:project with{Roles=project.Roles.Select(r=>r.Id==roleId?r with{Style=style}:r).ToArray()};
        }
        else if(name=="update_song_title")
        {
            var title=PatchTool(project.SongTitle,args.GetProperty("patch"));
            if(!Enum.IsDefined(title.Position)||title.Text?.Length>500||title.Text?.Any(char.IsControl)==true||!ProjectValidator.IsValidStyle(new SubtitleStyle(title.FontFamily,title.FontSize,28,title.Color,title.Color,title.OutlineColor,title.OutlineWidth,title.ShadowOffset,MarginRatio:title.MarginRatio)))throw new InvalidOperationException("曲名或其样式参数无效。");
            candidate=project with{SongTitle=title};
        }
        else throw new InvalidOperationException("未知工具。");
        var previous=_workspace;token.ThrowIfCancellationRequested();
        _workspace=_workspace with{Project=candidate with{ModifiedAtUtc=DateTimeOffset.UtcNow}};
        RecordHistory(previous);RefreshSubtitleStyleFields();RefreshLyricPreview(_playback.Position);
        StatusText.Text="AI 已更新项目样式，可撤销；尚未保存。";
        return name=="update_song_title"?(object)_workspace.Project.SongTitle:(selectedRole is null?_workspace.Project.SubtitleStyle:_workspace.Project.Roles.Single(r=>r.Id==roleId).Style);
    }
}
