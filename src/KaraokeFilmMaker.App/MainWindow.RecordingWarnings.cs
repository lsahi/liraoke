using System.Windows;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private readonly HashSet<(Guid ProjectId, Guid LineId)> _ignoredRecordingLanguageWarnings = [];
    private (Guid ProjectId, Guid LineId)? _shownRecordingLanguageWarning;

    private void RefreshRecordingLanguageWarning(int recordingLineIndex)
    {
        RecordingLanguageWarningPanel.Visibility = Visibility.Collapsed;
        _shownRecordingLanguageWarning = null;
        if (_workspace is null || recordingLineIndex < 0 || recordingLineIndex >= _workspace.Project.Lyrics.Lines.Count)
        { return; }

        var line = _workspace.Project.Lyrics.Lines[recordingLineIndex];
        var key = (_workspace.Project.Id, line.Id);
        if (_ignoredRecordingLanguageWarnings.Contains(key)) { return; }
        var advisorySpans = (line.LanguageSpans ?? []).Where(span => LanguageAnalysisService.IsRecordingAdvisory(line, span)).ToArray();
        if (advisorySpans.Length == 0) { return; }

        string languages = string.Join(" / ", advisorySpans.Select(span => span.LanguageId == LanguageIds.Unknown ? "未确定" : span.LanguageId).Distinct());
        RecordingLanguageWarningText.Text = $"第 {recordingLineIndex + 1} 句的语言识别不确定（{languages}），分词可能需要检查。可继续按空格打轴；如有问题，再用“语言 / 片段…”或歌词编辑修正。";
        _shownRecordingLanguageWarning = key;
        RecordingLanguageWarningPanel.Visibility = Visibility.Visible;
    }

    private void DismissRecordingLanguageWarning_Click(object sender, RoutedEventArgs e)
    {
        if (_shownRecordingLanguageWarning is { } key) { _ignoredRecordingLanguageWarnings.Add(key); }
        RecordingLanguageWarningPanel.Visibility = Visibility.Collapsed;
        _shownRecordingLanguageWarning = null;
        ProjectTimeline.Focus();
    }
}
