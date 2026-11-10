using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KaraokeFilmMaker.Application.Music;
using KaraokeFilmMaker.Infrastructure.Music;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App;

public partial class MusicSearchWindow : Window
{
    private readonly MusicSourceRegistry _sources;
    private readonly MusicAudioTransfer _transfer;
    private readonly UserPreferenceStore _preferences;
    private CancellationTokenSource? _operation;
    private PreparedMusicAudio? _prepared;
    private bool _closed;
    public PreparedMusicAudio? Selection { get; private set; }
    public MusicSearchWindow(MusicSourceRegistry sources, MusicAudioTransfer transfer, UserPreferenceStore preferences)
    {
        InitializeComponent(); _sources = sources; _transfer = transfer; _preferences = preferences;
        SourceFilter.ItemsSource = new[] { new MusicSourceDescriptor("", "所有音源", MusicSourceCapabilities.Search, MediaAuthenticationKind.None) }.Concat(sources.Sources.Select(x => x.Descriptor));
        SourceFilter.SelectedIndex = 0;
        AccountSource.ItemsSource = sources.Sources.Select(x => x.Descriptor).ToArray(); AccountSource.SelectedIndex = 0;
    }
    private MusicSourceCredentials Credentials(string source) => UserPreferenceStore.ReadMusicAccount(_preferences.Load().MusicAccounts.FirstOrDefault(x => x.SourceId == source));
    private void AccountSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_preferences is null || AccountSource.SelectedValue is not string source) return;
        try { var account = Credentials(source); CookieInput.Password = account.Cookie; SignerInput.Text = account.SignerEndpoint ?? ""; }
        catch (Exception ex) { CookieInput.Password = ""; SignerInput.Text = ""; StatusText.Text = ex.Message; }
        SignerPanel.Visibility = source == "qishui" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SaveAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountSource.SelectedValue is not string source || _operation is not null) return;
        try { _preferences.SaveMusicAccount(source, new(CookieInput.Password.Trim(), source == "qishui" ? SignerInput.Text.Trim() : null));
            StopAndClear(); RefreshButtons(); StatusText.Text = "该音源设置已加密保存；未验证账号是否有效。"; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void CheckSigner_Click(object sender, RoutedEventArgs e)
    {
        string endpoint = SignerInput.Text.Trim();
        await RunAsync(async token =>
        {
            StatusText.Text = "正在检查签名服务（不发送 Cookie）…";
            await new MusicHostClient().ExecuteAsync<bool>(new { action = "checkSigner", sourceId = "qishui", signerEndpoint = endpoint }, token);
            if (!_closed) StatusText.Text = "签名服务连接与响应格式检查通过。尚未验证签名有效性或歌曲权限；请保存此音源设置。";
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_operation is not null || _closed) return;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        SearchControls.IsEnabled = SettingsPanel.IsEnabled = ResultsGrid.IsEnabled = false;
        PreviewButton.IsEnabled = ImportButton.IsEnabled = false; CancelTaskButton.IsEnabled = true;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { if (!_closed) StatusText.Text = cancellation.IsCancellationRequested ? "已取消，原工程未改变。" : "请求超时，请重试。"; }
        catch (Exception ex) { if (!_closed) StatusText.Text = ex is InvalidOperationException or System.IO.InvalidDataException ? ex.Message : "请求失败，请检查网络、音源账号或磁盘空间。"; }
        finally
        {
            _operation = null;
            if (!_closed) { SearchControls.IsEnabled = SettingsPanel.IsEnabled = ResultsGrid.IsEnabled = true; CancelTaskButton.IsEnabled = false; RefreshButtons(); }
        }
    }
    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();
    private async void Query_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); } }
    private Task SearchAsync() => RunAsync(async token =>
    {
        StopAndClear(); ResultsGrid.ItemsSource = null; StatusText.Text = "正在搜索各音源…";
        string? source = SourceFilter.SelectedValue as string;
        var result = await _sources.SearchAsync(QueryInput.Text, string.IsNullOrEmpty(source) ? null : source, Credentials, token);
        if (_closed) return;
        ResultsGrid.ItemsSource = result.Tracks;
        StatusText.Text = $"找到 {result.Tracks.Count} 个结果（每个音源首批最多 30 首），保留原始歌名与版本。" + (result.Warnings.Count > 0 ? "\n" + string.Join("\n", result.Warnings) : "");
    });
    private void Results_Changed(object sender, SelectionChangedEventArgs e)
    {
        StopAndClear(); RefreshButtons();
        if (ResultsGrid.SelectedItem is MusicTrack track) StatusText.Text = $"{track.SourceLabel} · {track.Title} · {track.Artist}\n音频可用性尚未检查。";
    }
    private void RefreshButtons()
    {
        var capabilities = ResultsGrid.SelectedItem is MusicTrack track ? _sources.Get(track.SourceId).Descriptor.Capabilities : MusicSourceCapabilities.None;
        PreviewButton.IsEnabled = _operation is null && capabilities.HasFlag(MusicSourceCapabilities.Preview);
        ImportButton.IsEnabled = _operation is null && capabilities.HasFlag(MusicSourceCapabilities.ImportAudio) && (_prepared is null || _prepared.CanImport);
    }
    private async Task<PreparedMusicAudio> PrepareAsync(MusicTrack track, CancellationToken token)
    {
        if (_prepared?.Track == track) return _prepared;
        StopAndClear();
        var result = await _transfer.PrepareAsync(_sources.Get(track.SourceId), track, Credentials(track.SourceId),
            new Progress<string>(text => { if (!_closed && !token.IsCancellationRequested) StatusText.Text = text; }), token);
        if (_closed || token.IsCancellationRequested) { result.Dispose(); token.ThrowIfCancellationRequested(); throw new OperationCanceledException(); }
        _prepared = result; StatusText.Text = $"{result.Availability} · 音质 {result.Quality} · 实际 {result.DurationSeconds:0.0} 秒";
        return result;
    }
    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not MusicTrack track || !_sources.Get(track.SourceId).Descriptor.Capabilities.HasFlag(MusicSourceCapabilities.Preview)) return;
        await RunAsync(async token => { var audio = await PrepareAsync(track, token); Audition.Source = new Uri(audio.AudioPath); Audition.Play(); });
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not MusicTrack track || !_sources.Get(track.SourceId).Descriptor.Capabilities.HasFlag(MusicSourceCapabilities.ImportAudio)) return;
        await RunAsync(async token =>
        {
            var audio = await PrepareAsync(track, token);
            if (!audio.CanImport) return;
            if (MessageBox.Show(this, $"导入“{track.Title} / {track.Artist}”为项目歌曲？\n\n现有歌词与打轴保持不变。请确认录音版本和素材使用权限。", "导入音频",
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            Audition.Close(); Selection = audio; _prepared = null; DialogResult = true;
        });
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => Audition.Stop();
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void Audition_Failed(object sender, ExceptionRoutedEventArgs e) { Audition.Close(); StatusText.Text = "无法试听，请检查音频输出设备。"; }
    private void StopAndClear() { Audition.Close(); _prepared?.Dispose(); _prepared = null; }
    private void Window_Closed(object? sender, EventArgs e) { _closed = true; _operation?.Cancel(); StopAndClear(); CookieInput.Clear(); }
}
