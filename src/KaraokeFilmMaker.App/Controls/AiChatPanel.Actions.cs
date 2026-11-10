using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KaraokeFilmMaker.Infrastructure.Music;

namespace KaraokeFilmMaker.App.Controls;

public partial class AiChatPanel
{
    private readonly List<Action> _inlineCleanup = [];
    private Action? _stopAudition;
    private Action? _musicCleanup;
    public IProgress<string> CreateToolProgress(string text) { var notice = AddNotice(text); return new Progress<string>(value => notice.Text = value); }
    private TextBlock? _approvalStatus;
    public void ReportActionResult(string text) { if (_approvalStatus is not null) _approvalStatus.Text = text; }
    public void ClearInlineContent()
    {
        foreach (var cleanup in _inlineCleanup.ToArray()) cleanup();
        _inlineCleanup.Clear(); _stopAudition = null; _musicCleanup = null;
    }
    public void StopAudition() => _stopAudition?.Invoke();
    private StackPanel AddCard(string title, string detail)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = detail, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 6) });
        Messages.Children.Add(new Border { Child = content, Padding = new Thickness(8), Margin = new Thickness(0, 4, 0, 6),
            Background = new SolidColorBrush(Color.FromRgb(245, 249, 253)), BorderBrush = Brushes.LightBlue, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) });
        ConversationScroll.ScrollToEnd(); return content;
    }
    public async Task<bool> RequestApprovalAsync(string title, string detail, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var card = AddCard(title, detail);
        var row = new WrapPanel(); var approve = new Button { Content = "Approve · 执行", Margin = new Thickness(0, 0, 8, 0) };
        var reject = new Button { Content = "取消" }; var status = new TextBlock { Margin = new Thickness(0), TextWrapping = TextWrapping.Wrap };
        _approvalStatus = status;
        row.Children.Add(approve); row.Children.Add(reject); card.Children.Add(row); card.Children.Add(status);
        approve.Click += (_, _) => completion.TrySetResult(true);
        reject.Click += (_, _) => completion.TrySetResult(false);
        Action cleanup = () => completion.TrySetCanceled(); _inlineCleanup.Add(cleanup);
        using var registration = token.Register(() => completion.TrySetCanceled(token));
        try { bool accepted = await completion.Task; status.Text = accepted ? "已批准，正在执行…" : "已取消，未执行。"; return accepted; }
        catch (OperationCanceledException) { status.Text = "已停止，未执行。"; throw; }
        finally { approve.IsEnabled = reject.IsEnabled = false; _inlineCleanup.Remove(cleanup); }
    }
    public void ShowMusicCard(PreparedMusicAudio audio, Func<Task> beforePlay, Func<Task> import, Action released)
    {
        StopAudition(); _musicCleanup?.Invoke();
        if (_musicCleanup is not null) _inlineCleanup.Remove(_musicCleanup);
        var card = AddCard(audio.Track.Title, $"{audio.Track.Artist} · {audio.Track.SourceLabel}\n{audio.Quality} · {audio.DurationSeconds:F1} 秒\n{audio.Availability}");
        var player = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Width = 0, Height = 0 };
        var row = new WrapPanel(); var play = new Button { Content = "▶ 播放", Margin = new Thickness(0, 0, 6, 0) };
        var stop = new Button { Content = "停止", Margin = new Thickness(0, 0, 6, 0) };
        var add = new Button { Content = "导入歌曲…", IsEnabled = audio.CanImport };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0) };
        row.Children.Add(play); row.Children.Add(stop); row.Children.Add(add); card.Children.Add(row); card.Children.Add(status); card.Children.Add(player);
        bool disposed = false, importing = false, cleanupRequested = false;
        Action halt = () => { if (!disposed) { player.Stop(); play.Content = "▶ 播放"; } };
        play.Click += async (_, _) => {
            play.IsEnabled = false;
            try { StopAudition(); await beforePlay(); if (disposed) return; _stopAudition = halt; player.Source = new Uri(audio.AudioPath); player.Play(); play.Content = "播放中"; }
            catch (Exception) { status.Text = "无法播放，请检查音频输出设备。"; }
            finally { if (!disposed) play.IsEnabled = true; }
        };
        stop.Click += (_, _) => halt();
        player.MediaEnded += (_, _) => halt();
        player.MediaFailed += (_, _) => { halt(); status.Text = "无法播放，请检查音频输出设备。"; };
        add.Click += async (_, _) => { add.IsEnabled = false; importing = true; try { await import(); } catch (OperationCanceledException) { status.Text = "已取消导入。"; } catch (Exception) { status.Text = "未能导入，请检查工作区和任务状态。"; } finally { importing = false; if (cleanupRequested) audio.Dispose(); if (!disposed) add.IsEnabled = audio.CanImport; } };
        _musicCleanup = () => { halt(); disposed = true; cleanupRequested = true; player.Close(); play.IsEnabled = add.IsEnabled = stop.IsEnabled = false; if (!importing) audio.Dispose(); released(); };
        _inlineCleanup.Add(_musicCleanup);
    }
}
