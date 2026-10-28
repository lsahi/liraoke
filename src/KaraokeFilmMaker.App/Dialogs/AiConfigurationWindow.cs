using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KaraokeFilmMaker.Application.Preferences;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Dialogs;

/// <summary>Local configuration editor. Saving never tests or contacts an endpoint.</summary>
public sealed class AiConfigurationWindow : Window
{
    private readonly UserPreferenceStore _store;
    private readonly ListBox _providers = new() { DisplayMemberPath = nameof(AiProviderPreference.ProviderName) };
    private readonly TextBox _name = new() { MaxLength = 80 };
    private readonly ComboBox _official = new() { ItemsSource = new[] { "官方 API（true）", "供应商 / 中转站 API（false）" } };
    private readonly TextBox _path = new() { MaxLength = 4096 };
    private readonly PasswordBox _apiKey = new() { MaxLength = 16384 };
    private readonly PasswordBox _ak = new() { MaxLength = 16384 };
    private readonly PasswordBox _sk = new() { MaxLength = 16384 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly CheckBox _replaceCredentials = new()
    { Content = "重新输入并替换无法解密的凭据（留空表示清除）", Visibility = Visibility.Collapsed };
    private readonly Button _save = new() { Content = "保存配置", Padding = new Thickness(14, 7, 14, 7) };
    private Guid _id = Guid.NewGuid();
    private bool _loading, _dirty, _unreadable;

    public AiConfigurationWindow(UserPreferenceStore store)
    {
        _store = store;
        Title = "用户 AI 配置"; Width = 760; Height = 650; MinWidth = 650; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White; Foreground = new SolidColorBrush(Color.FromRgb(52, 66, 77));
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Liraoke;component/Themes/LightTheme.xaml") });
        var grid = new Grid { Margin = new Thickness(16), Background = Brushes.White };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new DockPanel(); grid.Children.Add(left);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(buttons, Dock.Top); left.Children.Add(buttons);
        var add = new Button { Content = "新增", Padding = new Thickness(12, 6, 12, 6) };
        var delete = new Button { Content = "删除", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(add); buttons.Children.Add(delete); left.Children.Add(_providers);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 2); grid.Children.Add(scroll);
        var form = new StackPanel(); scroll.Content = form;
        form.Children.Add(new TextBlock { Text = "AI 连接配置", FontSize = 19, FontWeight = FontWeights.SemiBold });
        form.Children.Add(new TextBlock { Text = "当前仅保存配置，AI暂未接入。", Margin = new Thickness(0, 6, 0, 4) });
        AddField(form, "providerName *　配置名称", _name, "AiProviderName");
        AddField(form, "isOfficial *　API 来源", _official, "AiIsOfficial");
        AddField(form, "apiPath　可选，完整 API 地址", _path, "AiApiPath");
        AddField(form, "APIKey　可选", _apiKey, "AiApiKey");
        AddField(form, "AK　可选", _ak, "AiAk");
        AddField(form, "SK　可选", _sk, "AiSk");
        form.Children.Add(new TextBlock { Text = "凭据由 Windows 当前用户加密保存。更换电脑或 Windows 用户后需重新输入。保存不会验证接口，也不会上传任何内容。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 10, 0, 10) });
        form.Children.Add(_replaceCredentials); form.Children.Add(_save); form.Children.Add(_status);
        Content = grid;
        _name.TextChanged += Changed; _path.TextChanged += Changed;
        _official.SelectionChanged += Changed; _apiKey.PasswordChanged += Changed;
        _ak.PasswordChanged += Changed; _sk.PasswordChanged += Changed;
        _replaceCredentials.Checked += Changed; _replaceCredentials.Unchecked += Changed;
        _save.Click += (_, _) => Save();
        add.Click += (_, _) => { if (CanDiscard()) LoadProvider(null); };
        delete.Click += (_, _) => Delete();
        _providers.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            var selected = _providers.SelectedItem as AiProviderPreference;
            if (!CanDiscard())
            {
                _loading = true; _providers.SelectedItem = _providers.Items.Cast<AiProviderPreference>().FirstOrDefault(p => p.Id == _id); _loading = false;
                return;
            }
            LoadProvider(selected);
        };
        Closing += OnClosing;
        try { Reload(); LoadProvider(null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { _status.Text = "无法读取用户偏好，未改动原文件。请检查文件格式和目录权限。"; _save.IsEnabled = false; add.IsEnabled = false; delete.IsEnabled = false; }
    }

    private static void AddField(Panel form, string label, Control field, string name)
    {
        field.Name = name; field.MinHeight = 30; field.Margin = new Thickness(0, 3, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(field, label);
        form.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 0) }); form.Children.Add(field);
    }
    private void Changed(object sender, RoutedEventArgs e) { if (!_loading) _dirty = true; }
    private bool CanDiscard() => !_dirty || MessageBox.Show(this, "有未保存的 AI 配置修改，是否放弃？", "用户 AI 配置",
        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    private void OnClosing(object? sender, CancelEventArgs e) { if (!CanDiscard()) e.Cancel = true; }
    private void Reload()
    {
        _loading = true;
        try { _providers.ItemsSource = _store.Load().AiProviders; }
        finally { _loading = false; }
    }
    private void LoadProvider(AiProviderPreference? provider)
    {
        _loading = true;
        try
        {
            _providers.SelectedItem = provider; _id = provider?.Id ?? Guid.NewGuid();
            _name.Text = provider?.ProviderName ?? ""; _official.SelectedIndex = provider is null ? -1 : provider.IsOfficial ? 0 : 1;
            _path.Text = provider?.ApiPath ?? ""; _apiKey.Clear(); _ak.Clear(); _sk.Clear();
            _unreadable = false; _status.Text = ""; _replaceCredentials.IsChecked = false; _replaceCredentials.Visibility = Visibility.Collapsed;
            if (provider is not null)
            {
                try
                {
                    var keys = UserPreferenceStore.ReadAiCredentials(provider);
                    _apiKey.Password = keys.APIKey ?? ""; _ak.Password = keys.AK ?? ""; _sk.Password = keys.SK ?? "";
                }
                catch (InvalidDataException e)
                { _unreadable = true; _replaceCredentials.Visibility = Visibility.Visible; _status.Text = e.Message; }
            }
            _dirty = false;
        }
        finally { _loading = false; }
    }
    private void Save()
    {
        if (_unreadable && _replaceCredentials.IsChecked != true)
        { _status.Text = "请先确认替换无法解密的凭据；原配置尚未修改。"; return; }
        try
        {
            _store.SaveAiProvider(_id, _name.Text, _official.SelectedIndex < 0 ? null : _official.SelectedIndex == 0,
                _path.Text, new AiCredentials(_apiKey.Password, _ak.Password, _sk.Password));
            _dirty = false; Reload(); LoadProvider(_providers.Items.Cast<AiProviderPreference>().Single(p => p.Id == _id));
            _status.Text = "已保存。AI暂未接入。";
        }
        catch (InvalidDataException e) { _status.Text = e.Message; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { _status.Text = "保存失败，请检查程序目录写入权限和 Windows 凭据保护功能。原配置未被替换。"; }
    }
    private void Delete()
    {
        if (_providers.SelectedItem is not AiProviderPreference provider || !CanDiscard()) return;
        if (MessageBox.Show(this, "删除选中的 AI 配置？", "用户 AI 配置", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { _store.DeleteAiProvider(provider.Id); _dirty = false; Reload(); LoadProvider(null); _status.Text = "配置已删除。"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _status.Text = "删除失败，请检查配置文件和程序目录权限。"; }
    }
}
