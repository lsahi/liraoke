using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Application.Preferences;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Controls;

public partial class AiChatPanel : UserControl
{
    public event EventHandler? CollapseRequested;
    public event EventHandler? ConfigurationRequested;
    private UserPreferenceStore? _store;
    private IAiChatService? _chat;
    private CancellationTokenSource? _sending;
    private readonly List<AiChatMessage> _history = [];
    private AiProviderPreference? _active;
    private bool _refreshing, _pendingConfigurationRefresh;
    public bool IsSending => _sending is not null;
    public AiChatPanel()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            bool compact = ActualHeight < 380;
            ChatBorder.Padding = new Thickness(compact ? 8 : 12);
            ChatHeader.Margin = new Thickness(0, 0, 0, compact ? 4 : 10);
            Composer.Margin = new Thickness(0, compact ? 4 : 10, 0, 0);
            SendRow.Margin = new Thickness(0, compact ? 2 : 8, 0, 0);
            MessageInput.Height = compact ? 32 : 76;
        };
    }
    public void Initialize(UserPreferenceStore store, IAiChatService chat, KaraokeFilmMaker.Infrastructure.Ai.AiStateStore? state = null) { _store = store; _chat = chat; _state=state??new(Path.Combine(Path.GetDirectoryName(store.FilePath)!,"ai-state")); RefreshConfigurations(); }
    public void Stop() => _sending?.Cancel();
    public void RefreshConfigurations()
    {
        if (_store is null) return;
        if (IsSending) { _pendingConfigurationRefresh=true; return; }
        _pendingConfigurationRefresh=false;
        try
        {
            var providers = _store.Load().AiProviders; _refreshing = true;
            ConfigurationPicker.ItemsSource = providers;
            ConfigurationPicker.SelectedItem = providers.FirstOrDefault(p => p.Id == _active?.Id) ?? providers.FirstOrDefault();
            _refreshing = false; SelectConfiguration();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { _refreshing = false; AddNotice("无法读取 AI 配置，请检查用户偏好文件。"); }
    }
    private void Configuration_Changed(object sender, SelectionChangedEventArgs e) { if (!_refreshing) SelectConfiguration(); }
    private void SelectConfiguration()
    {
        var selected = ConfigurationPicker.SelectedItem as AiProviderPreference;
        if (selected == _active) return;
        Stop(); _active = selected; RestoreConversation();
    }
    public void FocusInput() => MessageInput.Focus();
    private void Configure_Click(object sender, RoutedEventArgs e) => ConfigurationRequested?.Invoke(this, EventArgs.Empty);
    private void Collapse_Click(object sender, RoutedEventArgs e) => CollapseRequested?.Invoke(this, EventArgs.Empty);
    private void Send_Click(object sender, RoutedEventArgs e) { if (_sending is not null) Stop(); else Send(); }
    private void NewChat_Click(object sender, RoutedEventArgs e) { if(_sending is not null)return; try { _state?.ArchiveSession(SessionKey); _sessionReadable=true; _history.Clear(); ClearInlineContent(); Messages.Children.Clear(); AddNotice("已开始新对话，旧会话已归档；用户长期记忆保留。"); } catch(Exception error) when(error is IOException or UnauthorizedAccessException){AddNotice(error.Message);} }
    private void MessageInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true; if (_sending is null) Send();
    }
    private TextBlock AddNotice(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) };
        Messages.Children.Add(block); return block;
    }
    private async void Send()
    {
        string text = MessageInput.Text.Trim(); if (text.Length == 0 || _sending is not null) return;
        if(!_sessionReadable){AddNotice("请先处理会话保存问题或开始新对话。");return;}
        if (_active is not { } provider || _chat is null) { AddNotice("请先添加并选择 AI 配置。"); return; }
        try {
            if (_store?.ContainsCredential(text) == true) { MessageInput.Clear(); AddNotice("检测到疑似密钥，未发送。请在配置窗口填写凭据。"); ConfigurationRequested?.Invoke(this, EventArgs.Empty); return; }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AddNotice("凭据检查失败，请先检查配置；本条未发送。"); return; }
        AiCredentials credentials;
        try { credentials = UserPreferenceStore.ReadAiCredentials(provider); AiModelCatalog.ValidateConnection(provider, credentials); }
        catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException) { AddNotice(e.Message); return; }
        var context = AiContextWindow.Select(_history);
        _history.Add(new("user",text));
        try { PersistConversation(true); } catch(Exception e) when(e is IOException or UnauthorizedAccessException){_history.RemoveAt(_history.Count-1);AddNotice("会话未能保存，本条未发送："+e.Message);return;}
        using var cancel = new CancellationTokenSource(); _sending = cancel;
        ConfigurationPicker.IsEnabled = false; NewChatButton.IsEnabled = false; SendButton.Content = "停止";
        Messages.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(237, 247, 252)), CornerRadius = new CornerRadius(7), Padding = new Thickness(10),
            Margin = new Thickness(0, 8, 0, 8), Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } });
        var thinking = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Brushes.SlateGray };
        var expander = new Expander { Header = "正在思考…", ToolTip = "服务商返回的思考内容已过滤明显的内部指令引用。", Content = thinking, IsExpanded = true, Visibility = Visibility.Collapsed };
        Messages.Children.Add(expander);
        var answer = new TextBox { IsReadOnly = true, Text = "正在连接…", TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(0, 5, 0, 8) };
        Messages.Children.Add(answer); MessageInput.Clear();
        var response = new StringBuilder(); var reasoning = new StringBuilder(); bool changed = false, complete = false, thinkingStarted = false, bodyStarted = false;
        DateTime lastCheckpoint=DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        void Flush()
        {
            if (!changed) return; changed = false;
            bool bottom = ConversationScroll.VerticalOffset >= ConversationScroll.ScrollableHeight - 24;
            thinking.Text = reasoning.Length > 0 ? reasoning.ToString() : "正在整理可显示的思考内容…";
            expander.Visibility = thinkingStarted ? Visibility.Visible : Visibility.Collapsed;
            answer.Text = response.Length > 0 ? response.ToString() : "正在生成…";
            if (bottom) ConversationScroll.ScrollToEnd();
            if(DateTime.UtcNow-lastCheckpoint>TimeSpan.FromSeconds(5))
            {
                lastCheckpoint=DateTime.UtcNow;
                try { PersistConversation(true,response.ToString()); }
                catch(Exception error)when(error is IOException or UnauthorizedAccessException){AddNotice("会话检查点保存失败："+error.Message);}
            }
        }
        timer.Tick += (_, _) => Flush(); timer.Start();
        try
        {
            await foreach (var item in _chat.StreamAsync(provider, credentials, context, text, cancel.Token))
            {
                if (item.Type == "thinking_started") { thinkingStarted = true; changed = true; Flush(); }
                if (item.Type == "thinking") { thinkingStarted = true; reasoning.Append(item.Text); changed = true; }
                if (item.Type == "text")
                {
                    response.Append(item.Text); changed = true;
                    if (!bodyStarted && !string.IsNullOrWhiteSpace(item.Text))
                    {
                        bodyStarted = true; Flush(); expander.Header = "思考过程"; expander.IsExpanded = false;
                    }
                }
                if (item.Type is "tool_started" or "tool_result") RememberContext(item.Text);
                if (item.Type == "tool_started") AddNotice("工具请求已记录，正在等待批准或执行…");
                if (item.Type == "tool") AddNotice(item.Text);
                if (item.Type == "error") throw new InvalidOperationException(item.Text);
                if (item.Type == "done") { complete = true; if (item.Text.Length > 0) AddNotice(item.Text); }
            }
            if (!complete) throw new InvalidOperationException("未收到完整回复，请重试。");

        }
        catch (OperationCanceledException) { Flush(); AddNotice(cancel.IsCancellationRequested ? "已停止；上下文与已返回的工具结果已保留。" : "连接已中断；上下文与工具结果已保留，可继续对话。"); }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { Flush(); AddNotice(e.Message); }
        finally
        {
            timer.Stop(); Flush();
            if(provider==_active)
            {
                string answerText=response.ToString();
                answerText=SafeRecord(answerText);
                if(answerText.Length>0)_history.Add(new("assistant",answerText+(complete?"":"\n[回复中断，未完成]")));
                if(!complete)_history.Add(new("context","本轮回复中断。已返回的工具结果仍有效；无结果的工具状态未知，下一轮先查询，不得默认成功或重复执行。"));
                try { PersistConversation(false); }catch(Exception error)when(error is IOException or UnauthorizedAccessException){AddNotice("会话保存失败："+error.Message);}
            }
            if (response.Length == 0) answer.Text = "";
            expander.Header = complete ? "思考过程" : "思考过程（已中止）";
            _sending = null; ConfigurationPicker.IsEnabled = true; NewChatButton.IsEnabled = true; SendButton.Content = "发送";
            if (_pendingConfigurationRefresh) RefreshConfigurations();
        }
    }
}
