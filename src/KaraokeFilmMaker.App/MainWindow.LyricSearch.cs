using System.Net.Http;
using System.Windows;
using KaraokeFilmMaker.Infrastructure.Lyrics;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private async void SearchLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || _isBusy) { StatusText.Text = "请先打开工作区。"; return; }
        var master = _workspace.Project.MediaAssets.FirstOrDefault(asset => asset.Id == _workspace.Project.MasterAudioAssetId);
        string title = master is null ? _workspace.Project.Title : System.IO.Path.GetFileNameWithoutExtension(master.DisplayName);
        using var client = new HttpClient();
        var search = new LyricSearchWindow(new LrclibLyricSearchService(client), title,
            master?.Duration.TotalSeconds) { Owner = this };
        if (search.ShowDialog() != true || search.Selection is not { } selection) { return; }
        if (_lyricRows.Count > 0 && MessageBox.Show(this, "导入将替换当前歌词、读音和打轴数据，但可以撤销。是否继续？",
            "替换歌词", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
        await RunUiOperationAsync(async () =>
        {
            ApplyLyricRowsToProject();
            var lyrics = await new OnlineLyricImporter().ImportAsync(_workspace.RootPath, selection, GetSelectedLyricLanguage(), _operationCancellation!.Token);
            lyrics = await AnalyzeImportedLyricsAsync(lyrics);
            _workspace = _workspace with { Project = _workspace.Project with { Lyrics = lyrics, ModifiedAtUtc = DateTimeOffset.UtcNow } };
            ResetMarkers(); RefreshMarkerList(); RefreshLyricRows();
            StatusText.Text = $"已导入 {lyrics.Lines.Count} 行歌词，原文及来源已保存到 lyrics；请校对版本、读音和时间后保存项目。";
        });
    }
}
