using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Application.Preferences;
using KaraokeFilmMaker.Infrastructure.Ai;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Dialogs;

public sealed class AiConfigurationWindow : Window
{
    private readonly UserPreferenceStore _store;
    private readonly IAiChatService _chat;
    private readonly ListBox _providers = new() { DisplayMemberPath = nameof(AiProviderPreference.ProviderName) };
    private readonly ComboBox _model = new() { IsEditable = true, ItemsSource = AiModelCatalog.Presets, DisplayMemberPath = nameof(AiModelPreset.Model), IsTextSearchEnabled = false };
    private readonly TextBlock _providerHint = new() { Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _name = new() { MaxLength = 80 };
    private readonly ComboBox _official = new() { ItemsSource = new[] { "官方 API", "供应商 / 中转站 API" } };
    private readonly TextBox _path = new() { MaxLength = 4096 };
    private readonly ComboBox _protocol = new() { ItemsSource = new[] { "OpenAI Chat Completions", "DeepSeek（OpenAI 兼容）", "Claude Messages" }, SelectedIndex = 0 };
    private readonly PasswordBox _apiKey = new() { MaxLength = 16384 };
    // Retain legacy AK/SK without presenting unsupported signing schemes as working adapters.
    private AiCredentials _legacyKeys = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly CheckBox _replaceCredentials = new() { Content = "重新输入并替换无法解密的凭据（留空表示清除）", Visibility = Visibility.Collapsed };
    private readonly Button _save = new() { Content = "保存配置", Padding = new Thickness(14, 7, 14, 7) };
    private readonly Button _test = new() { Content = "联通测试", Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 8, 0) };
    private readonly StackPanel _keyField;
    private Guid _id = Guid.NewGuid();
    private bool _loading, _dirty, _unreadable;
    private int _revision;
    private CancellationTokenSource? _testing;
    private static readonly string[] Protocols = ["openai", "deepseek", "anthropic"];

    public AiConfigurationWindow(UserPreferenceStore store, IAiChatService? chat = null)
    {
        _store = store; _chat = chat ?? new PiCordisChatService();
        Title = "用户 AI 配置"; Width = 820; Height = 730; MinWidth = 670; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White; Foreground = new SolidColorBrush(Color.FromRgb(52, 66, 77));
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Liraoke;component/Themes/LightTheme.xaml") });
        var grid = new Grid { Margin = new Thickness(16), Background = Brushes.White };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new DockPanel(); grid.Children.Add(left);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(buttons, Dock.Top); left.Children.Add(buttons);
        var add = new Button { Content = "新增", Padding = new Thickness(12, 6, 12, 6) };
        var delete = new Button { Content = "删除", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(add); buttons.Children.Add(delete); left.Children.Add(_providers);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetColumn(scroll, 2); grid.Children.Add(scroll);
        var form = new StackPanel(); scroll.Content = form;
        form.Children.Add(new TextBlock { Text = "AI 连接配置", FontSize = 19, FontWeight = FontWeights.SemiBold });
        form.Children.Add(new TextBlock { Text = "选择常用模型，或直接输入供应商提供的模型名。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4) });
        AddField(form, "模型", _model, "AiModel"); form.Children.Add(_providerHint);
        AddField(form, "配置名称", _name, "AiProviderName");
        AddField(form, "API 来源", _official, "AiIsOfficial");
        AddField(form, "完整 API 地址", _path, "AiApiPath");
        AddField(form, "接口协议", _protocol, "AiProtocol");
        _keyField = AddField(form, "APIKey", _apiKey, "AiApiKey");
        form.Children.Add(new TextBlock { Text = "密钥只在配置窗口填写，请勿输入 AI 聊天框。凭据仅由当前 Windows 用户加密保存。联通测试会发送一条简短请求，可能消耗少量额度；保存配置本身不发送请求。Qwen 默认北京地域，其他地域请修改地址。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 12, 0, 12) });
        form.Children.Add(_replaceCredentials);
        var actions = new DockPanel(); DockPanel.SetDock(_test, Dock.Left); actions.Children.Add(_test); actions.Children.Add(_save);
        form.Children.Add(actions); form.Children.Add(_status); Content = grid;
        _save.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
              <Border Background="{TemplateBinding Background}" CornerRadius="6" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter Property="Opacity" Value="0.88"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        _name.TextChanged += Changed; _path.TextChanged += Changed; _protocol.SelectionChanged += Changed;
        _official.SelectionChanged += (_, e) => { if (_loading) return; FillOfficial(); Changed(this, e); };
        _model.SelectionChanged += (_, _) =>
        {
            if (_loading || _model.SelectedItem is not AiModelPreset preset) return;
            _loading = true;
            _model.Text = preset.Model; _name.Text = preset.Model; _protocol.SelectedIndex = Array.IndexOf(Protocols, preset.Protocol);
            if (_official.SelectedIndex < 0) _official.SelectedIndex = 0;
            _loading = false; FillOfficial(); ModelChanged(); Changed(this, new RoutedEventArgs());
        };
        _model.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, e) => { if (!_loading) { ModelChanged(); Changed(this, e); } }));
        _apiKey.PasswordChanged += Changed; _replaceCredentials.Checked += Changed; _replaceCredentials.Unchecked += Changed;
        _save.Click += (_, _) => Save(); _test.Click += async (_, _) => await TestAsync();
        add.Click += (_, _) => { if (CanDiscard()) LoadProvider(null); }; delete.Click += (_, _) => Delete();
        _providers.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            var selected = _providers.SelectedItem as AiProviderPreference;
            if (!CanDiscard()) { _loading = true; _providers.SelectedItem = _providers.Items.Cast<AiProviderPreference>().FirstOrDefault(p => p.Id == _id); _loading = false; return; }
            LoadProvider(selected);
        };
        Closing += (_, e) => { if (!CanDiscard()) e.Cancel = true; else _testing?.Cancel(); };
        try { Reload(); LoadProvider(null); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        { _status.Text = "无法读取用户偏好，未改动原文件。请检查文件格式和目录权限。"; _save.IsEnabled = false; _test.IsEnabled = false; add.IsEnabled = false; delete.IsEnabled = false; }
    }
    private static StackPanel AddField(Panel form, string label, Control field, string name)
    {
        field.Name = name; field.MinHeight = 30; field.Margin = new Thickness(0, 3, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(field, label);
        var group = new StackPanel(); group.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 0) }); group.Children.Add(field); form.Children.Add(group); return group;
    }
    private void FillOfficial()
    { if (_official.SelectedIndex == 0 && AiModelCatalog.Find(_model.Text) is { } p) _path.Text = p.Endpoint; }
    private void ModelChanged()
    {
        _keyField.Visibility = string.IsNullOrWhiteSpace(_model.Text) ? Visibility.Collapsed : Visibility.Visible;
        _providerHint.Text = AiModelCatalog.Find(_model.Text)?.ProviderId ?? "自定义模型 · 请按供应商说明选择接口协议和地址";
    }
    private void TestColor(bool? passed)
    {
        _save.Background = new SolidColorBrush(passed switch { true => Color.FromRgb(45, 130, 84), false => Color.FromRgb(247, 205, 91), _ => Color.FromRgb(55, 65, 75) });
        _save.Foreground = passed == false ? Brushes.Black : Brushes.White;
    }
    private void Changed(object sender, RoutedEventArgs e)
    { if (!_loading) { _dirty = true; _revision++; _testing?.Cancel(); TestColor(null); _status.Text = "配置已变更，尚未测试。"; } }
    private bool CanDiscard() => !_dirty || MessageBox.Show(this, "有未保存的 AI 配置修改，是否放弃？", "用户 AI 配置", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    private void Reload()
    { _loading = true; try { _providers.ItemsSource = _store.Load().AiProviders; } finally { _loading = false; } }
    private void LoadProvider(AiProviderPreference? provider)
    {
        _testing?.Cancel(); _revision++; _loading = true;
        try
        {
            _providers.SelectedItem = provider; _id = provider?.Id ?? Guid.NewGuid();
            _model.SelectedItem = null; _model.Text = provider?.Model ?? "";
            _name.Text = provider?.ProviderName ?? ""; _official.SelectedIndex = provider is null ? -1 : provider.IsOfficial ? 0 : 1;
            _protocol.SelectedIndex = Math.Max(0, Array.IndexOf(Protocols, provider?.Protocol ?? "openai"));
            _path.Text = provider?.ApiPath ?? ""; _apiKey.Clear(); _legacyKeys = new();
            _unreadable = false; _status.Text = "尚未测试"; _replaceCredentials.IsChecked = false; _replaceCredentials.Visibility = Visibility.Collapsed;
            if (provider is not null)
            {
                try { _legacyKeys = UserPreferenceStore.ReadAiCredentials(provider); _apiKey.Password = _legacyKeys.APIKey ?? ""; }
                catch (InvalidDataException e) { _unreadable = true; _replaceCredentials.Visibility = Visibility.Visible; _status.Text = e.Message; }
            }
            _dirty = false; ModelChanged(); TestColor(null);
        }
        finally { _loading = false; }
    }
    private AiProviderPreference Draft()
    {
        if (_unreadable && _replaceCredentials.IsChecked != true) throw new InvalidDataException("请先确认替换无法解密的凭据；原配置尚未修改。");
        if (_official.SelectedIndex < 0) throw new InvalidDataException("请选择 API 来源。");
        if (string.IsNullOrWhiteSpace(_model.Text)) throw new InvalidDataException("请选择或输入模型名称。");
        return new(_id, _name.Text.Trim(), _official.SelectedIndex == 0, _path.Text.Trim(), Model: _model.Text.Trim(), ProviderId: AiModelCatalog.Find(_model.Text)?.ProviderId ?? "custom", Protocol: Protocols[Math.Max(0, _protocol.SelectedIndex)]);
    }
    private AiCredentials Keys() => new(_apiKey.Password, _legacyKeys.AK, _legacyKeys.SK);
    private async Task TestAsync()
    {
        if (_testing is not null) { _testing.Cancel(); return; }
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(90)); _testing = cancel;
        int revision = _revision; _test.Content = "停止测试"; _status.Text = "正在验证模型的流式响应…"; _save.IsEnabled = false;
        try
        {
            bool success = false;
            await foreach (var item in _chat.StreamAsync(Draft(), Keys(), [], "请只回复 OK。", cancel.Token))
            { if (item.Type == "error") throw new InvalidOperationException(item.Text); if (item.Type == "done") success = true; }
            if (revision != _revision) return;
            TestColor(success); _status.Text = success ? "联通测试通过，可保存配置。" : "未收到完整响应。";
        }
        catch (OperationCanceledException) { if (revision == _revision) { TestColor(false); _status.Text = "测试已取消或超时，可重试。"; } }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { if (revision == _revision) { TestColor(false); _status.Text = e.Message; } }
        finally { _testing = null; _test.Content = "联通测试"; _save.IsEnabled = true; }
    }
    private void Save()
    {
        try
        {
            var p = Draft(); var color = _save.Background; var foreground = _save.Foreground;
            _store.SaveAiProvider(p.Id, p.ProviderName, p.IsOfficial, p.ApiPath, Keys(), p.Model, p.ProviderId, p.Protocol);
            _dirty = false; Reload(); LoadProvider(_providers.Items.Cast<AiProviderPreference>().Single(x => x.Id == _id));
            _save.Background = color; _save.Foreground = foreground; _status.Text = "已保存配置。";
        }
        catch (InvalidDataException e) { _status.Text = e.Message; }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { _status.Text = "保存失败，请检查程序目录写入权限和 Windows 凭据保护功能。原配置未被替换。"; }
    }
    private void Delete()
    {
        if (_providers.SelectedItem is not AiProviderPreference p || !CanDiscard()) return;
        if (MessageBox.Show(this, "删除选中的 AI 配置？", "用户 AI 配置", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { _store.DeleteAiProvider(p.Id); _dirty = false; Reload(); LoadProvider(null); _status.Text = "配置已删除。"; }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { _status.Text = "删除失败，请检查配置文件和程序目录权限。"; }
    }
}
