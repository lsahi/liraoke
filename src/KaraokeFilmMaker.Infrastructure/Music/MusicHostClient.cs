using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace KaraokeFilmMaker.Infrastructure.Music;

public sealed class MusicHostClient(string? baseDirectory = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<T> ExecuteAsync<T>(object request, CancellationToken cancellationToken = default)
    {
        string root = baseDirectory ?? AppContext.BaseDirectory;
        string node = Path.Combine(root, "tools", "ai-runtime", "node.exe");
        string script = Path.Combine(root, "tools", "music-runtime", "host.mjs");
        if (!File.Exists(node) || !File.Exists(script)) throw new InvalidOperationException("找歌组件未安装。开发环境请运行 scripts/bootstrap-music.ps1；用户请使用完整安装包。");
        var start = new ProcessStartInfo(node) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(script)! };
        start.ArgumentList.Add(script);
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("NODE_", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("QISHUI_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        try { if (!process.Start()) throw new InvalidOperationException("找歌组件启动失败。"); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("找歌组件无法启动。"); }
        using var registration = timeout.Token.Register(() => Kill(process));
        Task drain = Task.Run(async () => { char[] buffer = new char[1024]; try { while (await process.StandardError.ReadAsync(buffer) > 0) { } } catch (IOException) { } });
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Json).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            var output = new StringBuilder(); char[] buffer = new char[4096]; int count;
            while ((count = await process.StandardOutput.ReadAsync(buffer, timeout.Token)) > 0)
            {
                output.Append(buffer, 0, count);
                if (output.Length > 1_000_000) throw new InvalidDataException("找歌响应过大。");
            }
            timeout.Token.ThrowIfCancellationRequested();
            using var json = JsonDocument.Parse(output.ToString());
            if (!json.RootElement.GetProperty("ok").GetBoolean())
            {
                string? code = json.RootElement.GetProperty("code").GetString();
                throw new InvalidOperationException(code switch
                {
                    "QISHUI_SETUP" => "汽水获取音频需要账号 Cookie 和可信的签名服务，请在音源设置中配置。",
                    "SIGNER_ENDPOINT" => "请输入兼容 Meting 的签名接口完整地址（HTTPS 或本机回环 HTTP，不含用户信息或片段）。",
                    "SIGNER_AUTH" => "签名服务拒绝认证（401/403）。当前接口未配置服务专用认证，不能直接使用需要额外认证头的服务。",
                    "SIGNER_REDIRECT" => "签名服务发生重定向。请填写最终接口地址，程序不会自动转发签名请求。",
                    "SIGNER_HTTP" => "签名服务返回错误状态，请检查服务是否可用。",
                    "SIGNER_PROTOCOL" => "签名服务响应不符合 Meting 契约：需要 ok=true 及有效的 X-Helios、X-Medusa 字符串。",
                    "SIGNER_TIMEOUT" => "签名服务超过 15 秒未响应，请检查服务后重试。",
                    "SIGNER_NETWORK" => "无法连接签名服务，请检查地址、网络和证书。",
                    "NO_AUDIO" => "音源未提供可用音频。可能需要登录、会员权限，或当前歌曲不支持获取。",
                    _ => "音源请求失败，请检查账号状态、网络连接后重试。",
                });
            }
            return json.RootElement.GetProperty("result").Deserialize<T>(Json) ?? throw new InvalidDataException("找歌响应为空。");
        }
        catch (JsonException) { throw new InvalidDataException("找歌组件返回格式错误。"); }
        finally { Kill(process); await process.WaitForExitAsync(CancellationToken.None); await drain; }
    }
    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
