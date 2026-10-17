using System.Windows;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Infrastructure.Languages;
using Microsoft.Win32;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private readonly LanguagePluginLoader _languagePluginLoader = new();
    private LanguageRegistry _languageRegistry = null!;
    private LanguageAnalysisService _languageAnalysis = null!;

    private void InitializeLanguages()
    {
        _languageRegistry = LanguageRegistry.CreateBuiltIns(_japaneseReadingService);
        _languageAnalysis = new LanguageAnalysisService(_languageRegistry);
    }

    private async Task<LyricDocument> AnalyzeImportedLyricsAsync(LyricDocument lyrics) =>
        await Task.Run(() => _languageAnalysis.AnalyzeDocumentAsync(lyrics,
            cancellationToken: _operationCancellation?.Token ?? default));

    private async void LoadLanguagePlugin_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) { return; }
        var dialog = new OpenFileDialog { Title = "加载语言插件（本次运行）", Filter = "语言插件 (*.dll)|*.dll", CheckFileExists = true };
        if (!_dialogLocations.ShowDialog(dialog, Dialogs.DialogLocation.ImportLanguagePlugin, this)) { return; }
        if (MessageBox.Show(this,
            "语言插件会执行第三方代码，拥有当前用户的权限；依赖隔离不是安全沙箱。只加载你信任的作者提供的文件。\n\n插件仅在本次运行中加载，不会自动下载或在下次启动时执行；现有项目不会自动重新分析。\n\n继续加载？",
            "信任语言插件", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        await RunUiOperationAsync(async () =>
        {
            var added = await Task.Run(() => _languagePluginLoader.Load(dialog.FileName));
            _languageRegistry = new LanguageRegistry(_languageRegistry.Adaptors.Concat(added));
            _languageAnalysis = new LanguageAnalysisService(_languageRegistry);
            StatusText.Text = $"已加载：{string.Join("、", added.Select(item => item.DisplayName))}。现有歌词和打轴未改动；新导入自动参与识别，也可在“语言 / 片段”中指定。";
        });
    }

    private async void EditLanguageRegions_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditRecordingStructure()) { return; }
        int index = RecordingEditLineIndex();
        if (index < 0 || index >= _workspace!.Project.Lyrics.Lines.Count) { return; }
        if (_playback.IsPlaying) { await _playback.PauseAsync(); PlayPauseButton.Content = "播放"; }
        LyricLine line = _workspace.Project.Lyrics.Lines[index];
        var choices = _languageRegistry.Adaptors.Where(item => item.LanguageId != LanguageIds.Unknown)
            .Select(item => new LanguageChoice(item.LanguageId, $"{item.DisplayName} ({item.LanguageId})", item.Capabilities.SupportsManualReading)).ToArray();
        var spans = line.LanguageSpans ?? (line.PrimaryText.Length == 0 ? [] : new[]
        {
            new LyricLanguageSpan(Guid.NewGuid(), new TextRange(0, line.PrimaryText.Length),
                LanguageIds.FromLegacy(_workspace.Project.Lyrics.Language) ?? LanguageIds.Unknown, LanguageDetectionSource.DefaultHint),
        });
        var summaries = spans.Select(span => new LanguageRegionSummary(span.Range,
            line.PrimaryText.Substring(span.Range.Start, span.Range.Length), span.LanguageId,
            (span.IsLocked ? "人工锁定" : span.Source == LanguageDetectionSource.DefaultHint ? "继承主要语言提示"
                : span.Source == LanguageDetectionSource.Context ? "上下文推断" : "自动识别")
                + (LanguageAnalysisService.IsRecordingAdvisory(line, span) ? " · 建议确认（可打轴） · " + span.Diagnostic
                    : span.NeedsReview ? " · 待确认 · " + span.Diagnostic : ""), span.ManualReading)).ToArray();
        TextRange? selected = null;
        if (_recordingEditTarget is { } target && line.Units.FirstOrDefault(unit => unit.Id == target.UnitId) is { } chosen
            && chosen.DisplayRanges.Count > 0)
        {
            int start = chosen.DisplayRanges.Min(range => range.Start), end = chosen.DisplayRanges.Max(range => range.EndExclusive);
            selected = new TextRange(start, end - start);
        }
        var editor = new LanguageRegionEditorWindow(line.PrimaryText, choices, summaries, selected) { Owner = this };
        if (editor.ShowDialog() != true || editor.RequestedEdit is not { } edit) { return; }
        if (!CanEditLanguageContent(line, edit.Range)) { return; }
        var protectedUnits = line.Units.Where(unit => (unit.IsConfirmed || unit.RawInputTime is not null)
            && unit.DisplayRanges.Any(range => Overlaps(range, edit.Range))).ToArray();
        if (protectedUnits.Length > 0 && MessageBox.Show(this,
            $"选区涉及 {protectedUnits.Length} 个已打轴单元。更换语言 / 读音会重新建立受影响片段的单元，需要重新校准这些时间；选区外的单元将保留。此操作可撤销。\n\n是否继续？",
            "重新分析选中片段", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        await RunUiOperationAsync(async () =>
        {
            string? hint = LanguageIds.FromLegacy(_workspace.Project.Lyrics.Language);
            LyricLine updated = await Task.Run(() => edit.LanguageId is { } language
                ? _languageAnalysis.SetLanguageAsync(line, edit.Range, language, edit.Reading, hint, _operationCancellation!.Token)
                : _languageAnalysis.RedetectRangeAsync(line, edit.Range, hint, _operationCancellation!.Token));
            var lines = _workspace.Project.Lyrics.Lines.ToArray(); lines[index] = updated;
            CommitRecordingLines(lines, updated.Id);
            StatusText.Text = edit.LanguageId is null ? "选区已重新自动识别；请检查语言提示、读音和分组，可撤销。"
                : "选区语言 / 读音已应用并锁定，选区外打轴保留；请检查新分组，可撤销。";
        });
    }

    private static bool Overlaps(TextRange left, TextRange right) => left.Start < right.EndExclusive && right.Start < left.EndExclusive;

    private bool CanEditLanguageContent(LyricLine line, TextRange? replacement = null)
    {
        string[] missing = (line.LanguageSpans ?? []).Where(span => span.LanguageId != LanguageIds.Unknown
            && _languageRegistry.Find(span.LanguageId) is null
            && (replacement is not { } range || span.Range.Start < range.Start || span.Range.EndExclusive > range.EndExclusive))
            .Select(span => span.LanguageId).Distinct().ToArray();
        if (missing.Length == 0) { return true; }
        StatusText.Text = $"此句需要先加载语言插件：{string.Join("、", missing)}，才能修改文字 / 读音或重新分组。现有打轴、时间调整、预览、导出和保存不受影响。";
        return false;
    }

    private static string LanguageSummary(LyricLine? line)
    {
        if (line?.LanguageSpans is not { Count: > 0 } spans) { return ""; }
        string languages = string.Join(" / ", spans.Select(span => span.LanguageId).Distinct());
        int advisory = spans.Count(span => LanguageAnalysisService.IsRecordingAdvisory(line, span));
        int review = spans.Count(span => span.NeedsReview && !LanguageAnalysisService.IsRecordingAdvisory(line, span));
        return $" · {languages}"
            + (review > 0 ? $" · {review} 段待确认（语言 / 片段）" : "")
            + (advisory > 0 ? $" · {advisory} 段建议确认（可打轴）" : "");
    }
}
