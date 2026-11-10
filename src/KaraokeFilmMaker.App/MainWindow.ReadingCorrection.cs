using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.App.Dialogs;
using KaraokeFilmMaker.Infrastructure.Ai;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private IReadingCorrectionService? _readingCorrectionService = null;
    private void RefreshAsrAvailability()
    {
        if (AsrPanel is null) return;
        var doc = _workspace?.Project.Lyrics;
        AsrPanel.Visibility = doc is not null && doc.Lines.Any(l => ReadingCorrection.IsJapanese(l, doc.Language)) ? Visibility.Visible : Visibility.Collapsed;
    }
    private void AsrConfiguration_Click(object sender, RoutedEventArgs e) => ShowAsrConfiguration();
    private bool ShowAsrConfiguration() => new AsrConfigurationWindow(_userPreferenceStore) { Owner = this }.ShowDialog() == true;
    private async void CorrectReadings_Click(object sender, RoutedEventArgs e)
    {
        try { await StartReadingCorrectionAsync(false, CancellationToken.None); }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        { StatusText.Text = error.Message; }
    }
    private async Task<object> StartReadingCorrectionAsync(bool fromAgent, CancellationToken token)
    {
        if (_workspace is null || _isBusy) throw new InvalidOperationException("请先打开项目，并等待当前任务完成。");
        CheckAiDrafts();
        var project = _workspace.Project;
        var asset = project.MediaAssets.FirstOrDefault(a => a.Id == project.MasterAudioAssetId)
            ?? throw new InvalidOperationException("请先导入歌曲。");
        var targets = project.Lyrics.Lines.Where(l => ReadingCorrection.IsJapanese(l, project.Lyrics.Language)).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("当前项目没有可校正的日语歌词。");
        var prefs = _userPreferenceStore.Load();
        if (prefs.Asr is null || !prefs.AiProviders.Any(p => p.Id == prefs.Asr.CorrectionProviderId))
        {
            if (!ShowAsrConfiguration()) return new { executed = false, reason = "尚未配置 ASR 与校正模型。" };
            prefs = _userPreferenceStore.Load();
        }
        var settings = prefs.Asr!;
        var mapper = prefs.AiProviders.First(p => p.Id == settings.CorrectionProviderId);
        var credentials = UserPreferenceStore.ReadAsrCredentials(settings);
        var mapperCredentials = UserPreferenceStore.ReadAiCredentials(mapper);
        var diagnostics = new AsrDiagnosticLog();
        diagnostics.Record("configuration", "checking", category: settings.Protocol == "qwen-chat" ? "qwen-chat" : settings.Protocol == "transcriptions" ? "transcriptions" : "unknown-protocol");
        try {
            QwenReadingCorrectionService.ValidateSettings(settings, credentials);
            AiModelCatalog.ValidateConnection(mapper, mapperCredentials);
        } catch (InvalidOperationException e) {
            diagnostics.Failure("configuration", e);
            throw new InvalidOperationException(e.Message + DiagnosticLocation(diagnostics));
        }
        if (fromAgent)
        {
            string snapshot = ProjectContent(project);
            if (!await AssistantPanel.RequestApprovalAsync("AI 发音校正", "读取歌曲音频与歌词，按 ASR 配置发送到识别服务，按文本模型配置校正读音。逐句应用并保存，可撤销；人工确认内容与已有打点保留为建议。", token))
                return new { approved = false, executed = false };
            CheckAiProject(project.Id); CheckAiDrafts();
            if (snapshot != ProjectContent(_workspace!.Project)) throw new InvalidOperationException("项目已修改，请重新发起校正。");
        }
        var chatProgress = fromAgent ? AssistantPanel.CreateToolProgress("正在准备发音校正…") : null;
        EditorTabs.SelectedItem = RecordingTab;
        int updated = 0, succeeded = 0, failed = 0; bool cancelled = false, completed = false, saved = false; string? failure = null; var failures = new List<string>();
        await RunAiOperationAsync(async () =>
        {
            if (_playback.IsPlaying) { await _playback.PauseAsync(); PlayPauseButton.Content = "播放"; }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _operationCancellation!.Token);
            using var http = new HttpClient(diagnostics.Wrap(new HttpClientHandler { AllowAutoRedirect = false })) { Timeout = TimeSpan.FromMinutes(4) };
            var service = _readingCorrectionService ?? new QwenReadingCorrectionService(_mediaTools.FfmpegPath, http, diagnostics);
            var received = new HashSet<Guid>();
            bool reportProgress = true;
            var progress = new Progress<string>(message => { if (reportProgress) { AsrStatusText.Text = message; StatusText.Text = message; chatProgress?.Report(message); } });
            try
            {
                string path = Path.GetFullPath(Path.Combine(_workspace!.RootPath, asset.RelativePath));
                await foreach (var result in service.CorrectAsync(path, asset.Duration.TotalSeconds, project.Lyrics,
                    settings, credentials, mapper, mapperCredentials, progress, linked.Token))
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (_workspace?.Project.Id != project.Id || _isClosing) throw new OperationCanceledException();
                    int index = _workspace.Project.Lyrics.Lines.ToList().FindIndex(l => l.Id == result.LineId);
                    if (index < 0 || !received.Add(result.LineId)) continue;
                    var source = _workspace.Project.Lyrics.Lines[index];
                    LyricLine changed;
                    try { changed = result.Proposal is null ? ReadingCorrection.Failed(source, result.Error ?? "识别失败")
                        : await ReadingCorrection.ApplyAsync(source, result.Proposal, _languageAnalysis, result.Evidence, linked.Token); }
                    catch (Exception e) when (e is InvalidOperationException or ArgumentException)
                    { diagnostics.Failure("apply", e, index + 1); changed = ReadingCorrection.Failed(source, QwenReadingCorrectionService.DescribeFailure(e)); }
                    if (changed.AiReadingReview?.Status == "failed") {
                        failure ??= changed.AiReadingReview.Message;
                        failures.Add($"第 {index + 1} 句：{changed.AiReadingReview.Message}");
                    }
                    else succeeded++;
                    diagnostics.Record("apply", changed.AiReadingReview?.Status == "failed" ? "failed" : "completed", index: index + 1);
                    var lines = _workspace.Project.Lyrics.Lines.ToArray(); lines[index] = changed;
                    CommitRecordingLines(lines, source.Id); updated++;
                }
                completed = true;
            }
            catch (OperationCanceledException e) { cancelled = true; diagnostics.Failure("workflow", e); }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException or IOException or System.Text.Json.JsonException)
            { diagnostics.Failure("workflow", e); failure = QwenReadingCorrectionService.DescribeFailure(e); }
            reportProgress = false;
            // Preserve completed rows on cancellation; persist a visible review marker on every unprocessed target.
            if (_workspace?.Project.Id == project.Id)
            {
                var lines = _workspace.Project.Lyrics.Lines.Select(l => targets.Any(t => t.Id == l.Id) && !received.Contains(l.Id)
                    ? ReadingCorrection.Failed(l, cancelled ? "校正已取消，此句尚未处理。" : failure ?? "AI 未返回此句结果，可重试或编辑读音。") : l).ToArray();
                CommitRecordingLines(lines, _browsedRecordingLineId);
                try { await SaveAiWorkspaceAsync(); saved = true; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { saved = false; diagnostics.Failure("save", e); }
            }
            failed = targets.Length - succeeded;
            completed = completed && failed == 0;
            string state = cancelled ? "已取消" : completed ? "校正完成" : succeeded > 0 ? "部分校正完成" : "校正失败";
            diagnostics.Record("workflow", cancelled ? "cancelled" : completed ? "completed" : "failed-or-partial", count: succeeded);
            AsrStatusText.Text = $"{state} · 已获得结果 {succeeded}/{targets.Length} 句 · {failed} 句失败／未处理。{(saved ? "已保存，可撤销。" : "请手动保存。")}" +
                (failures.Count > 0 ? "\n已跳过：" + string.Join("；", failures.Take(3)) + (failures.Count > 3 ? "\n更多原因请点击对应句的“校正详情”。" : "") : failure is null ? "" : "\n" + failure) + DiagnosticLocation(diagnostics);
            StatusText.Text = AsrStatusText.Text;
            chatProgress?.Report(AsrStatusText.Text);
            if (fromAgent) AssistantPanel.ReportActionResult(AsrStatusText.Text);
        });
        return new { executed = updated > 0, completed, cancelled, saved, processed = updated, succeeded, failed,
             message = AsrStatusText.Text };
    }
    private static string DiagnosticLocation(AsrDiagnosticLog log) => log.IsAvailable
        ? "\n诊断日志：" + log.FilePath : "\n诊断日志无法写入，请检查本地目录权限或磁盘空间。";
    private static string ReadingReviewSummary(LyricLine? line) => line?.AiReadingReview is not { } review ? "" : review.Status switch
    { "confirmed" => " · 读音已手动确认", "failed" => " · 校正失败：" + review.Message, "protected" => " · 已保留人工内容", _ => " · AI 读音已应用" };

    private void NextReadingReview_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || !CanLeaveQuickEdit()) return;
        var lines = _workspace.Project.Lyrics.Lines; int current = RecordingEditLineIndex();
        for (int offset = 1; offset <= lines.Count; offset++)
        {
            int next = (Math.Max(0, current) + offset) % lines.Count;
            if (lines[next].AiReadingReview is not { Status: "failed" }) continue;
            _browsedRecordingLineId = lines[next].Id; _lastTapHint = null; RefreshTapHint(_playback.Position); return;
        }
        StatusText.Text = "没有校正失败的句子。";
    }
    private void ReviewReading_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || _isBusy || !CanLeaveQuickEdit()) return;
        int index = RecordingEditLineIndex(); if (index < 0 || index >= _workspace.Project.Lyrics.Lines.Count) return;
        var source = _workspace.Project.Lyrics.Lines[index];
        if (source.AiReadingReview is not { } review) { StatusText.Text = "此句尚未进行 AI 校正。"; return; }
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            Text = $"原文：{review.OriginalText}\n当前：{source.PrimaryText}\n{(review.Status == "review" ? "AI 读音已应用。" : review.Message)}\n\nAI 建议：{review.SuggestedText}\n" +
                string.Join(" / ", review.Suggestions.Select(s => $"{s.Surface}【{s.Reading}】")) + "\n\n" + (review.Status == "failed" ? string.Join("\n", review.Issues.Select(i => i.Reason).Distinct()) : "") });
        panel.Children.Add(new TextBlock { Text = "成功结果已应用；失败或已保护的句子保留原内容。可在打轴页编辑读音或撤销校正。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        panel.Children.Add(new Button { Content = "返回编辑", IsCancel = true });
        new Window { Owner = this, Title = $"第 {index + 1} 句 · 校正详情", Width = 580, Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = panel } }.ShowDialog();
    }
}
