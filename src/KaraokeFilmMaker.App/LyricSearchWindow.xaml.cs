using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Infrastructure.Lyrics;

namespace KaraokeFilmMaker.App;

public partial class LyricSearchWindow : Window
{
    private readonly ILyricSearchService _service;
    private readonly double? _songDuration;
    private CancellationTokenSource? _searchCancellation;
    private bool _closed;
    public LyricImportSelection? Selection { get; private set; }

    public LyricSearchWindow(ILyricSearchService service, string suggestedTitle, double? songDuration)
    {
        InitializeComponent(); _service = service; _songDuration = songDuration;
        TitleInput.Text = suggestedTitle; SearchTabs.SelectedItem = LrclibTab;
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_searchCancellation is not null) { return; }
        using var cancellation = new CancellationTokenSource(); _searchCancellation = cancellation;
        SearchButton.IsEnabled = false; CancelSearchButton.IsEnabled = true;
        ResultsGrid.ItemsSource = null; SearchStatus.Text = "正在搜索…";
        try
        {
            var results = await _service.SearchAsync(new LyricSearchQuery(TitleInput.Text, ArtistInput.Text), cancellation.Token);
            if (_closed) { return; }
            ResultsGrid.ItemsSource = results.OrderBy(item => _songDuration.HasValue ? Math.Abs(item.DurationSeconds - _songDuration.Value) : 0).ToArray();
            SearchStatus.Text = results.Count == 0 ? "未找到结果；可尝试歌曲原名、减少关键词，或粘贴已授权歌词 / 从文件导入。" : $"找到 {results.Count} 个版本；请手动选择并核对。";
        }
        catch (OperationCanceledException) { if (!_closed) { SearchStatus.Text = "搜索已取消。"; } }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or ArgumentException or System.IO.IOException or System.IO.InvalidDataException)
        { if (!_closed) { SearchStatus.Text = exception is HttpRequestException ? "搜索失败：" + exception.Message + " 请检查网络，或粘贴已授权歌词 / 从文件导入。" : exception.Message; } }
        finally
        {
            _searchCancellation = null;
            if (!_closed) { SearchButton.IsEnabled = true; CancelSearchButton.IsEnabled = false; }
        }
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultPreviewText is null) { return; }
        RightsCheck.IsChecked = false;
        if (ResultsGrid.SelectedItem is not OnlineLyricResult result)
        { ResultPreviewText.Clear(); ResultInfoText.Text = "请选择一个结果。"; return; }
        UseTimingCheck.IsEnabled = result.HasTiming;
        UseTimingCheck.IsChecked = result.HasTiming;
        string difference = _songDuration is { } duration ? $" · 与歌曲时长相差 {Math.Abs(result.DurationSeconds - duration):0.0} 秒" : "";
        ResultInfoText.Text = $"{result.Title} — {result.Artist}\n专辑：{result.Album}\n来源：LRCLIB · {result.DurationDescription}{difference}";
        RefreshPreview();
    }

    private void UseTimingCheck_Click(object sender, RoutedEventArgs e) => RefreshPreview();
    private void RefreshPreview()
    {
        if (ResultsGrid.SelectedItem is not OnlineLyricResult result) { return; }
        ResultPreviewText.Text = result.Instrumental ? "该版本标记为纯音乐，没有可导入的歌词。"
            : UseTimingCheck.IsChecked == true && result.HasTiming ? result.SyncedLyrics! : result.PlainLyrics ?? "该版本没有纯文本；请保留 LRC 时间。";
    }

    private void ImportSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryPrepareImport()) { DialogResult = true; }
    }

    private bool TryPrepareImport()
    {
        Selection = null;
        if (RightsCheck.IsChecked != true) { ImportStatus.Text = "请先确认你具有歌词使用权限。"; return false; }
        if (SearchTabs.SelectedItem == LrclibTab)
        {
            if (ResultsGrid.SelectedItem is not OnlineLyricResult result || !result.CanImport)
            { ImportStatus.Text = "请选择一个有正文的歌词版本。"; return false; }
            bool lrc = UseTimingCheck.IsChecked == true && result.HasTiming;
            string? text = lrc ? result.SyncedLyrics : result.PlainLyrics;
            if (string.IsNullOrWhiteSpace(text)) { ImportStatus.Text = "此版本没有所选格式的歌词。"; return false; }
            Selection = new LyricImportSelection(text, lrc, result.Provider, result.Title, result.Artist, result.SourceUrl);
        }
        else if (SearchTabs.SelectedItem == PasteTab)
        {
            if (string.IsNullOrWhiteSpace(PasteLyricsInput.Text)) { ImportStatus.Text = "请粘贴歌词文本。"; return false; }
            string source = PasteSourceInput.Text.Trim();
            if (source.Length > 0 && (!Uri.TryCreate(source, UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length > 0))
            { ImportStatus.Text = "来源应为无账号凭据的 HTTPS 网页地址。"; return false; }
            Selection = new LyricImportSelection(PasteLyricsInput.Text, PasteTimingCheck.IsChecked == true,
                "User supplied", TitleInput.Text.Trim(), ArtistInput.Text.Trim(), source.Length == 0 ? null : source);
        }
        else { ImportStatus.Text = "请选择搜索结果或粘贴歌词。"; return false; }
        return true;
    }
    private void CancelSearchButton_Click(object sender, RoutedEventArgs e) => _searchCancellation?.Cancel();
    private void Window_Closed(object? sender, EventArgs e) { _closed = true; _searchCancellation?.Cancel(); }
}
