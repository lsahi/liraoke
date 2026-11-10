using System.Net;
using System.Net.Sockets;
using System.Text;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Application.Preferences;
using KaraokeFilmMaker.Infrastructure.Ai;

namespace KaraokeFilmMaker.App.Tests;

public sealed class PiCordisProcessTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    public async Task ClientRunsBundledHostAndCancelsItsNetworkStream(bool cancel, bool useTools, bool longTool)
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "tools/ai-runtime/node.exe")), "Run scripts/bootstrap-ai.ps1 before building tests.");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var requestCancel = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            for(int round=0;round<(useTools?2:1);round++)
            {
            using var client = await listener.AcceptTcpClientAsync(limit.Token); using var stream = client.GetStream();
            using var input = new MemoryStream(); byte[] buffer = new byte[8192]; bool unusedConnection = false;
            while (true)
            {
                int read = await stream.ReadAsync(buffer, limit.Token); if (read == 0) { if(input.Length == 0) { unusedConnection = true; break; } throw new IOException("Closed during request."); } input.Write(buffer, 0, read);
                string bytes = Encoding.Latin1.GetString(input.ToArray()); int end = bytes.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0) continue;
                var lengthLine = bytes[..end].Split("\r\n").Single(s => s.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase));
                int length = int.Parse(lengthLine.Split(':')[1]); if (input.Length < end + 4 + length) continue;
                Assert.Contains("/exact-completion", bytes[..end]);
                string body = Encoding.UTF8.GetString(input.ToArray(), end + 4, length); Assert.Contains("stream", body); if(useTools && round==1)Assert.Contains("tool-result-marker",body); break;
            }
            // HTTP pools can open and close an unused speculative connection.
            if(unusedConnection) { round--; continue; }
            string sse = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"思考\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n";
            if(useTools && round==0)sse="data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call1\",\"function\":{\"name\":\"get_project_context\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n";
            if (!cancel) sse += "data: [DONE]\n\n";
            string headers = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n";
            if(longTool && !useTools)await Task.Delay(2200,limit.Token);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(headers + sse), limit.Token);
            if (cancel)
            {
                try { Assert.Equal(0, await stream.ReadAsync(buffer, limit.Token)); }
                catch (IOException) { /* Killed child may reset rather than gracefully close TCP. */ }
            }
            }
        }, limit.Token);
        var events = new List<AiChatEvent>();
        async Task Consume()
        {
            await foreach (var e in new PiCordisChatService(toolFactory: useTools ? () => new AiToolBridge(async(_,_,token)=>{ if(longTool)await Task.Delay(2200,token);return new { title="tool-result-marker" };}) : null).StreamAsync(new(Guid.NewGuid(), "test", false,
                $"http://127.0.0.1:{port}/exact-completion", Model:"mock"), new("fake-key"), [], "你好", requestCancel.Token))
            { events.Add(e); if (cancel && e.Type == "text") requestCancel.Cancel(); }
        }
        try
        {
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Consume); else await Consume().WaitAsync(limit.Token);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Events: {System.Text.Json.JsonSerializer.Serialize(events)}; server: {server.Status}; {server.Exception}",error);
        }
        finally { requestCancel.Cancel(); }
        await server.WaitAsync(limit.Token);
        Assert.Contains(events, e => e.Type == "text" && e.Text == "你好");
        Assert.Contains(events, e => e.Type == "thinking" && e.Text == "思考");
        if(useTools)Assert.Contains(events,e=>e.Type=="tool");
        if (!cancel) Assert.Equal("done", events[^1].Type);
    }
}
