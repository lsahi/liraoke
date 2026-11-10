using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Application.Preferences;
using KaraokeFilmMaker.Infrastructure.Ai;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Dialogs;

public sealed class AsrConfigurationWindow : Window
{
    public AsrConfigurationWindow(UserPreferenceStore store)
    {
        Title = "AI 发音校正 · ASR 配置"; Width = 560; Height = 660;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MaxHeight = SystemParameters.WorkArea.Height;
        var prefs = store.Load(); var saved = prefs.Asr ?? new();
        var panel = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "音频识别与读音校正", FontSize = 20, FontWeight = FontWeights.SemiBold });
        void Label(string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 4) });
        Label("运行方式");
        var source = new ComboBox { ItemsSource = new[] { "云端 API", "本地已部署服务" }, SelectedIndex = saved.Local ? 1 : 0 }; panel.Children.Add(source);
        Label("ASR 接口协议");
        var protocol = new ComboBox { ItemsSource = new[] { "Qwen ASR / Omni · Chat Completions 音频输入", "OpenAI 兼容 Audio Transcriptions" }, SelectedIndex = saved.Protocol == "transcriptions" ? 1 : 0 }; panel.Children.Add(protocol);
        Label("完整 ASR 接口地址"); var endpoint = new TextBox { Text = saved.Endpoint }; panel.Children.Add(endpoint);
        Label("ASR 模型"); var model = new TextBox { Text = saved.Model }; panel.Children.Add(model);
        var omni = new Button { Content = "使用 Qwen-Omni 假名识别", Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(omni);
        omni.Click += (_, _) => { model.Text = "qwen3.8-omni-flash"; protocol.SelectedIndex = 0; };
        Label("API Key（本地服务可留空）"); var key = new PasswordBox(); panel.Children.Add(key);
        try { key.Password = UserPreferenceStore.ReadAsrCredentials(saved).APIKey ?? ""; }
        catch (System.IO.InvalidDataException) { }
        Label("读音校正文本模型（使用已保存的 AI 配置）");
        var provider = new ComboBox { DisplayMemberPath = "ProviderName", ItemsSource = prefs.AiProviders.Where(p => p.Protocol is "openai" or "deepseek").ToArray() };
        provider.SelectedItem = prefs.AiProviders.FirstOrDefault(p => p.Id == saved.CorrectionProviderId);
        if (provider.SelectedIndex < 0 && provider.Items.Count > 0) provider.SelectedIndex = 0;
        panel.Children.Add(provider);
        var configure = new Button { Content = "管理文本模型配置…", Margin = new Thickness(0, 8, 0, 0) }; panel.Children.Add(configure);
        configure.Click += (_, _) => {
            new AiConfigurationWindow(store) { Owner = this }.ShowDialog();
            provider.ItemsSource = store.Load().AiProviders.Where(p => p.Protocol is "openai" or "deepseek").ToArray();
            if (provider.Items.Count > 0) provider.SelectedIndex = 0;
        };
        Label("云端 ASR 会接收歌曲音频；校正文本模型会接收歌词与识别结果。本地模式只连接已有服务，不安装环境。\n密钥只在配置窗口填写，请勿输入 AI 聊天框。凭据由当前 Windows 用户加密保存。");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkGoldenrod, Margin = new Thickness(0, 8, 0, 8) }; panel.Children.Add(status);
        var save = new Button { Content = "保存 ASR 配置", Padding = new Thickness(12, 8, 12, 8), Background = Brushes.DimGray, Foreground = Brushes.White }; panel.Children.Add(save);
        save.Click += (_, _) => {
            try {
                if (provider.SelectedItem is not AiProviderPreference chosen) throw new InvalidOperationException("请先添加一个读音校正文本模型配置。");
                var value = new AsrPreference(source.SelectedIndex == 1, endpoint.Text.Trim(), model.Text.Trim(),
                    CorrectionProviderId: chosen.Id, Protocol: protocol.SelectedIndex == 1 ? "transcriptions" : "qwen-chat");
                QwenReadingCorrectionService.ValidateSettings(value, new(key.Password));
                store.SaveAsr(value, new(key.Password)); DialogResult = true;
            } catch (Exception e) when (e is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException) { status.Text = e.Message; }
        };
        source.SelectionChanged += (_, _) => {
            key.Clear();
            if (source.SelectedIndex == 1) { endpoint.Text = "http://127.0.0.1:8000/v1/audio/transcriptions"; model.Text = "Qwen/Qwen3-ASR-1.7B"; protocol.SelectedIndex = 1; }
            else { endpoint.Text = new AsrPreference().Endpoint; model.Text = "qwen3-asr-flash"; protocol.SelectedIndex = 0; }
        };
    }
}
