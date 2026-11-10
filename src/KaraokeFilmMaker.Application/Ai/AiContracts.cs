using KaraokeFilmMaker.Application.Preferences;

namespace KaraokeFilmMaker.Application.Ai;

public sealed record AiModelPreset(string Model, string ProviderId, string Protocol, string Endpoint);
public static class AiModelCatalog
{
    public static IReadOnlyList<AiModelPreset> Presets { get; } = [
        new("deepseek-flash", "DeepSeek", "deepseek", "https://api.deepseek.com/chat/completions"),
        new("deepseek-v4-pro", "DeepSeek", "deepseek", "https://api.deepseek.com/chat/completions"),
        new("gpt-4.1-mini", "ChatGPT / OpenAI", "openai", "https://api.openai.com/v1/chat/completions"),
        new("gpt-4.1", "ChatGPT / OpenAI", "openai", "https://api.openai.com/v1/chat/completions"),
        new("claude-sonnet-4-6", "Claude", "anthropic", "https://api.anthropic.com/v1/messages"),
        new("claude-haiku-4-5", "Claude", "anthropic", "https://api.anthropic.com/v1/messages"),
        new("kimi-k3.1-pro", "Kimi", "openai", "https://api.moonshot.ai/v1/chat/completions"),
        new("qwen-plus", "Qwen", "openai", "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"),
        new("qwen-turbo", "Qwen", "openai", "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions")
    ];
    public static AiModelPreset? Find(string? model) => Presets.FirstOrDefault(p => p.Model == model);
    public static void ValidateConnection(AiProviderPreference provider, AiCredentials credentials)
    {
        if (string.IsNullOrWhiteSpace(provider.Model)) throw new InvalidOperationException("请选择或输入模型名称。");
        if (provider.Protocol is not ("openai" or "deepseek" or "anthropic")) throw new InvalidOperationException("请选择支持的 API 协议。");
        if (!Uri.TryCreate(provider.ApiPath, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("请填写完整 HTTPS 接口地址；本机服务可使用 HTTP。地址中不能包含账户密码或片段。");
        if (credentials.APIKey is { } key && (key.Length > 16384 || key.Any(char.IsControl))) throw new InvalidOperationException("APIKey 格式无效，请在配置窗口重新填写。");
        if (string.IsNullOrWhiteSpace(credentials.APIKey)) throw new InvalidOperationException("此模型需要 APIKey。AK / SK 不能代替 APIKey。");
    }
}
public sealed record AiChatMessage(string Role, string Text);
public sealed record AiChatEvent(string Type, string Text);
public interface IAiChatService
{
    IAsyncEnumerable<AiChatEvent> StreamAsync(AiProviderPreference provider, AiCredentials credentials,
        IReadOnlyList<AiChatMessage> history, string prompt, CancellationToken cancellationToken = default);
}
