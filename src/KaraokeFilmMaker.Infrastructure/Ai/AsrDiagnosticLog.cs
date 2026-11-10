using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace KaraokeFilmMaker.Infrastructure.Ai;

/// <summary>Diagnostic metadata plus explicitly requested ASR transcripts; never log credentials, reasoning, or raw HTTP bodies.</summary>
public sealed class AsrDiagnosticLog
{
    private readonly object _gate = new();
    private long _metadataCharacters;
    private static readonly JsonSerializerOptions LogJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string FilePath { get; }
    public bool IsAvailable { get; private set; } = true;
    public AsrDiagnosticLog(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaraokeFilmMaker", "logs", "asr");
        FilePath = Path.Combine(directory, $"asr-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        Record("session", "started");
    }

    public void Record(string stage, string outcome, int? index = null, int? count = null,
        double? elapsedMs = null, int? httpStatus = null, string? category = null)
        => Write(new { utc = DateTimeOffset.UtcNow, stage, outcome, index, count, elapsedMs, httpStatus, category });

    public void Transcript(int index, double startSeconds, double endSeconds, string text, bool complete)
        => Write(new { utc = DateTimeOffset.UtcNow, stage = "asr-transcript", outcome = complete ? "completed" : "partial",
            index, startSeconds, endSeconds, text }, transcript: true);

    private void Write(object entry, bool transcript = false)
    {
        lock (_gate)
        {
            if (!IsAvailable) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string json = JsonSerializer.Serialize(entry, LogJson);
                // Transcript size is already bounded by the ASR reader. Never truncate a returned segment.
                if (!transcript && (_metadataCharacters += json.Length) > 2_000_000) return;
                File.AppendAllText(FilePath, json + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { IsAvailable = false; }
        }
    }

    public void Failure(string stage, Exception error, int? index = null) =>
        Record(stage, "failed", index, httpStatus: error is ReadingServiceHttpException http ? http.StatusCode : null,
            category: error is KaraokeFilmMaker.Application.Ai.ReadingMappingException mapping ? mapping.Code : error is ReadingServiceFailure classified ? classified.Code.ToString() : error is OperationCanceledException ? "cancelled-or-timeout" : error is HttpRequestException ? "network" :
                error is System.Text.Json.JsonException ? "invalid-json" : error is ReadingServiceHttpException ? "http" : error.GetType().Name);

    public HttpMessageHandler Wrap(HttpMessageHandler inner) => new DiagnosticHandler(this, inner);

    private sealed class DiagnosticHandler(AsrDiagnosticLog log, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            // Only classify known paths. User-defined hosts/paths can themselves contain credentials.
            string path = request.RequestUri?.AbsolutePath.TrimEnd('/') ?? "";
            string route = path.EndsWith("/chat/completions", StringComparison.Ordinal) ? "chat-completions" :
                path.EndsWith("/audio/transcriptions", StringComparison.Ordinal) ? "audio-transcriptions" :
                path.EndsWith("/v1", StringComparison.Ordinal) ? "base-url-only" : "custom-route";
            log.Record("http", "started", category: route);
            try
            {
                var response = await base.SendAsync(request, token);
                log.Record("http", "response", elapsedMs: watch.Elapsed.TotalMilliseconds,
                    httpStatus: (int)response.StatusCode, category: route);
                return response;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
            { log.Failure("http", e); throw; }
        }
    }
}

public sealed class ReadingServiceHttpException(int statusCode) : InvalidOperationException(
    $"服务请求失败（HTTP {statusCode}）：" + (statusCode switch {
        400 or 405 or 415 or 422 => "请检查接口协议、模型及音频请求格式。",
        401 => "认证未通过，请检查 API Key 及所属地域。",
        403 => "访问被拒绝，请检查模型权限及业务空间。",
        404 => "接口或模型未找到，请检查完整地址和模型名。",
        429 => "请求限流或额度不足，请检查账户后重试。",
        >= 500 => "服务端暂时不可用，请稍后重试。",
        _ => "请检查接口配置后重试。"
    }))
{
    public int StatusCode { get; } = statusCode;
    public bool ShouldStop => StatusCode is 400 or 401 or 403 or 404 or 405 or 415 or 422 or 429;
}
