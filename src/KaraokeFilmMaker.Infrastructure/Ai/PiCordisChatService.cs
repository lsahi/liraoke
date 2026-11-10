using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Application.Preferences;

namespace KaraokeFilmMaker.Infrastructure.Ai;

/// <summary>One isolated Pi/Cordis process per turn. Credentials travel only through private stdin.</summary>
public sealed class PiCordisChatService(string? runtimeDirectory = null, Func<IAiToolExecutor>? toolFactory = null, Func<IReadOnlyList<string>>? memoryFactory = null) : IAiChatService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public async IAsyncEnumerable<AiChatEvent> StreamAsync(AiProviderPreference provider, AiCredentials credentials,
        IReadOnlyList<AiChatMessage> history, string prompt, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AiModelCatalog.ValidateConnection(provider, credentials);
        if (history.Count > 100 || history.Sum(m => m.Text.Length) + prompt.Length > 120000)
            throw new InvalidOperationException("对话较长，请点击“新对话”后继续。");
        string root = runtimeDirectory ?? Path.Combine(AppContext.BaseDirectory, "tools", "ai-runtime");
        string node = Path.Combine(root, "node.exe"), host = Path.Combine(root, "host.mjs");
        if (!File.Exists(node) || !File.Exists(host)) throw new InvalidOperationException("AI 运行时未就绪，请使用完整安装包；开发环境请运行 scripts/bootstrap-ai.ps1。");
        var start = new ProcessStartInfo(node)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add(host);
        // Do not inherit runtime injection flags or environment-based provider credentials.
        foreach (string name in start.Environment.Keys.Where(k => k.StartsWith("NODE_", StringComparison.OrdinalIgnoreCase)
            || k.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(name);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new InvalidOperationException("AI 运行时启动失败。"); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("AI 运行时无法启动，请检查完整安装包。"); }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = lifetime.Token;
        using var registration = token.Register(() => Kill(process));
        // Drain stderr without logging provider responses or process internals.
        var drain = Task.Run(async () => { char[] buffer = new char[1024]; try { while (await process.StandardError.ReadAsync(buffer) > 0) { } } catch (IOException) { } });
        try
        {
            var executor = toolFactory?.Invoke(); int toolCalls = 0;
            var request = new { userMemory = memoryFactory?.Invoke() ?? [], tools = executor?.Definitions, connection = new { model = provider.Model, protocol = provider.Protocol,
                endpoint = provider.ApiPath, apiKey = credentials.APIKey }, history, prompt };
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Json).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            bool terminal = false; int total = 0;
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(token);
                if (line is null) break;
                total += line.Length;
                if (total > 8_000_000) throw new InvalidOperationException("AI 回复超过长度限制。");
                using (var packet = JsonDocument.Parse(line))
                {
                    if (packet.RootElement.GetProperty("type").GetString() == "tool_call")
                    {
                        var call = packet.RootElement;
                        string id = call.GetProperty("id").GetString()!;
                        string name = call.GetProperty("name").GetString()!;
                        object result;
                        yield return new AiChatEvent("tool_started", JsonSerializer.Serialize(new {tool=name,arguments=call.GetProperty("arguments"),status="requested"},Json));
                        try
                        {
                            if (executor is null || ++toolCalls > 20) throw new InvalidOperationException("工具未开放或达到本轮20次调用上限。");
                            token.ThrowIfCancellationRequested();
                            // Tool execution and human approval have their own cancellation, not a model-response deadline.
                            result = new { ok = true, result = await executor.ExecuteAsync(name, call.GetProperty("arguments").Clone(), token) };
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { result = new { ok = false, error = e is InvalidOperationException or ArgumentException ? e.Message : "操作失败，项目未确认变更；请检查客户端。" }; }
                        string reply = JsonSerializer.Serialize(new { type = "tool_result", id, result }, Json);
                        if(reply.Length > 128000) reply = JsonSerializer.Serialize(new { type = "tool_result", id, result = new { ok = false, error = "工具结果过长，请缩小读取范围。" } }, Json);
                        yield return new AiChatEvent("tool_result",JsonSerializer.Serialize(new {tool=name,result},Json));
                        await process.StandardInput.WriteLineAsync(reply.AsMemory(), token);
                        await process.StandardInput.FlushAsync(token);
                        yield return new AiChatEvent("tool", $"工具：{name} · 已返回执行结果");
                        continue;
                    }
                }
                AiChatEvent item;
                try { item = JsonSerializer.Deserialize<AiChatEvent>(line, Json) ?? throw new JsonException(); }
                catch (JsonException) { throw new InvalidOperationException("AI 运行时返回了无效数据。"); }
                if (item.Type is not ("text" or "thinking" or "thinking_started" or "done" or "error")) continue;
                terminal = item.Type is "done" or "error";
                yield return item;
                if (terminal) break;
            }
            token.ThrowIfCancellationRequested();
            if (!terminal) throw new InvalidOperationException("AI 运行时意外退出，请重试。");
        }
        finally { Kill(process); await process.WaitForExitAsync(CancellationToken.None); await drain; }
    }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
