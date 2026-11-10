using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Application.Preferences;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Infrastructure.Ai;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.Infrastructure.Tests;
public sealed class QwenReadingCorrectionTests
{
    [Fact] public async Task CloudUsesAudioPayloadAndParsesSseWithoutLeakingCredentials()
    {
        using var http = new HttpClient(new Handler(async request => {
            Assert.Equal("secret-test-only", request.Headers.Authorization!.Parameter);
            string body = await request.Content!.ReadAsStringAsync();
            Assert.DoesNotContain("secret-test-only", body);
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("asr_options").GetProperty("enable_itn").GetBoolean());
            Assert.StartsWith("data:audio/wav;base64,", doc.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("input_audio").GetProperty("data").GetString());
            return Sse("な", "さ");
        }));
        var service = new QwenReadingCorrectionService("unused", http); var result = new StringBuilder();
        await foreach (var chunk in service.TranscribeAsync([1, 2], new(), new("secret-test-only"), default)) result.Append(chunk);
        Assert.Equal("なさ", result.ToString());
    }
    [Fact] public async Task LocalMultipartNeedsNoKeyAndErrorsAreSanitized()
    {
        using var http = new HttpClient(new Handler(request => {
            Assert.Null(request.Headers.Authorization); Assert.IsType<MultipartFormDataContent>(request.Content);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("private-provider-secret") });
        }));
        var service = new QwenReadingCorrectionService("unused", http);
        var error = await Assert.ThrowsAsync<ReadingServiceHttpException>(async () => {
            await foreach (var _ in service.TranscribeAsync([1], new(true, "http://localhost:8000/v1/audio/transcriptions", Protocol: "transcriptions"), new(), default)) { }
        });
        Assert.Contains("401", error.Message); Assert.DoesNotContain("private-provider-secret", error.Message);
    }
    [Fact] public async Task TruncatedSseDoesNotLookSuccessful()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"な\"}}]}\n\n") })));
        await Assert.ThrowsAsync<ReadingServiceFailure>(async () => {
            await foreach (var _ in new QwenReadingCorrectionService("unused", http).TranscribeAsync([1], new(), new("fake"), default)) { }
        });
    }
    [Fact] public void CredentialsAreEncryptedAndSurviveStyleEdits()
    {
        string root = Path.Combine(Path.GetTempPath(), "AsrPrefs-" + Guid.NewGuid());
        try {
            var store = new UserPreferenceStore(root); store.SaveAsr(new(), new("asr-secret-123456"));
            store.SaveStyle("Keep", KaraokeProject.Create("test", DateTimeOffset.UtcNow).SubtitleStyle);
            Assert.Equal("asr-secret-123456", UserPreferenceStore.ReadAsrCredentials(store.Load().Asr!).APIKey);
            Assert.DoesNotContain("asr-secret-123456", File.ReadAllText(store.FilePath));
            Assert.True(store.ContainsCredential("please use asr-secret-123456"));
            Assert.False(store.ContainsCredential("打开 ASR 配置"));
        } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory] [InlineData(200, false)] [InlineData(200, true)] [InlineData(401, false)] [InlineData(404, false)] [InlineData(429, false)] [InlineData(-1, false)] public async Task RealFfmpegPipelineStreamsValidatedLineProposals(int status, bool array)
    {
        string root = Path.Combine(Path.GetTempPath(), "AsrAudio-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try {
            string audio = Path.Combine(root, "歌.wav");
            using (var w = new BinaryWriter(File.Create(audio))) {
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(32036); w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
                w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(32000); w.Write(new byte[32000]);
            }
            var lyrics = new LyricImportService().Parse("星空", ".txt", LyricLanguage.Japanese); int calls = 0;
            var proposal = new ReadingProposal(lyrics.Lines[0].Id, "星空", "星空", [new("星空", "なさ")]);
            using var http = new HttpClient(new Handler(async r => {
                                string body = await r.Content!.ReadAsStringAsync(); calls++;
                if (status == -1) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"途中\"}}]}\n\n") };
                if (status != 200) return new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent("secret-provider-body") };
                if (calls == 1) { Assert.Contains("audio/wav", body); return Sse("なさ"); }
                Assert.Contains("observations", body);
                using var requestJson = JsonDocument.Parse(body);
                Assert.Equal("disabled", requestJson.RootElement.GetProperty("thinking").GetProperty("type").GetString());
                Assert.Equal(8192, requestJson.RootElement.GetProperty("max_tokens").GetInt32());
                if (calls == 2) return Sse(JsonSerializer.Serialize(new { lines = new[] { new { lineId = "1", reading = "なさ", uncertain = false } } }));
                string json = JsonSerializer.Serialize(new { lineId = "1", originalText = proposal.OriginalText, text = proposal.Text, segments = proposal.Segments }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + "\n";
                if (array) json = "[" + json + "]";
                return Sse(json[..(json.Length / 2)], json[(json.Length / 2)..]);
            }));
            var log = new AsrDiagnosticLog(Path.Combine(root, "logs")); var service = new QwenReadingCorrectionService(MediaToolchain.Find().FfmpegPath, http, log);
            List<ReadingUpdate> result = [];
            async Task Run() {
                await foreach (var update in service.CorrectAsync(audio, status is 200 or -1 ? 1 : 120, lyrics, new(), new("fake"),
                    new(Guid.NewGuid(), "test", true, "https://example.invalid/chat", Model: "deepseek-v4-pro", Protocol: "deepseek"), new("fake"), new Progress<string>(), default)) result.Add(update);
            }
            if (status == 200) {
                await Run(); Assert.Equal(3, calls); Assert.Equal("なさ", Assert.Single(Assert.Single(result).Proposal!.Segments).Reading);
            } else if (status == -1) {
                await Run(); Assert.Equal(1, calls); Assert.Null(Assert.Single(result).Proposal);
                Assert.Contains("连接提前结束", result[0].Error);
            } else {
                var error = await Assert.ThrowsAsync<ReadingServiceHttpException>(Run);
                Assert.Equal(status, error.StatusCode); Assert.DoesNotContain("secret", error.Message);
                Assert.Equal(1, calls); Assert.Empty(result);
            }
            var transcript = File.ReadAllLines(log.FilePath).Select(l => JsonSerializer.Deserialize<JsonElement>(l))
                .Single(l => l.GetProperty("stage").GetString() == "asr-transcript");
            Assert.Equal(status == 200 ? "なさ" : status == -1 ? "途中" : "", transcript.GetProperty("text").GetString());
            Assert.Equal(status == 200 ? "completed" : "partial", transcript.GetProperty("outcome").GetString());
            Assert.Equal(0, transcript.GetProperty("startSeconds").GetDouble());
            Assert.DoesNotContain("secret-provider-body", File.ReadAllText(log.FilePath));
            if (status == -1) Assert.Contains("IncompleteStream", File.ReadAllText(log.FilePath));
        } finally { Directory.Delete(root, true); }
    }
    [Fact] public async Task OmniRequestsKanaAndDisablesThinkingWithoutAsrOptions()
    {
        using var http = new HttpClient(new Handler(async request => {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.False(body.RootElement.TryGetProperty("asr_options", out _));
            Assert.Equal("none", body.RootElement.GetProperty("reasoning_effort").GetString());
            Assert.Equal("text", body.RootElement.GetProperty("modalities")[0].GetString());
            var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
            Assert.Contains("ひらがな", content[0].GetProperty("text").GetString());
            Assert.Equal("input_audio", content[1].GetProperty("type").GetString());
            return Sse("すみ");
        }));
        var result = new StringBuilder();
        await foreach (var delta in new QwenReadingCorrectionService("unused", http).TranscribeAsync([1], new(Model: "qwen3.8-omni-flash"), new("fake"), default)) result.Append(delta);
        Assert.Equal("すみ", result.ToString());
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task FailedPartitionsAndBadLinesDoNotDiscardLaterWork(bool failedPartitions)
    {
        string root = Path.Combine(Path.GetTempPath(), "ReadingIsolation-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try {
            string path = Path.Combine(root, "song.wav");
            using (var w = new BinaryWriter(File.Create(path))) {
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(32036); w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
                w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(32000); w.Write(new byte[32000]);
            }
            var lyrics = new LyricImportService().Parse(string.Join("\n", Enumerable.Repeat("星空", 9)), ".txt", LyricLanguage.Japanese);
            int call = 0;
            using var http = new HttpClient(new Handler(async req => {
                call++; if (call == 1) return Sse("なさ");
                if (failedPartitions && call <= 3) throw new IOException("do-not-log-raw-provider-secret");
                using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
                using var input = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
                var targets = input.RootElement.GetProperty("targets").EnumerateArray().ToArray();
                if (body.RootElement.TryGetProperty("response_format", out _)) return Sse(JsonSerializer.Serialize(new {
                    lines = targets.Select(x => new {lineId=x.GetProperty("lineId").GetString(),reading="なさ",uncertain=false}) }));
                return Sse(string.Join("\n", targets.Select(x => JsonSerializer.Serialize(new {
                    lineId=x.GetProperty("lineId").GetString(), originalText="星空", text="星空",
                    segments=new[] {new ReadingSegment(!failedPartitions && x.GetProperty("lineId").GetString()=="2" ? "wrong" : "星空", "なさ")}
                }))));
            }));
            List<ReadingUpdate> updates = [];
            await foreach(var update in new QwenReadingCorrectionService(MediaToolchain.Find().FfmpegPath,http).CorrectAsync(path,1,lyrics,new(),new("fake"),
                new(Guid.NewGuid(),"test",true,"https://example.invalid/chat",Model:"test"),new("fake"),new Progress<string>(),default)) updates.Add(update);
            Assert.Equal(9,updates.Count);
            Assert.NotNull(updates.Single(x=>x.LineId==lyrics.Lines[8].Id).Proposal);
            Assert.All(updates,x=>Assert.NotNull(x.Proposal));
            Assert.DoesNotContain(updates,x=>x.Error?.Contains("secret")==true);
        } finally {Directory.Delete(root,true);}
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task MappingCanImproveIntermediateReadingAndRepairMissingAnnotation(bool missingAnnotation, bool repairFails)
    {
        string root = Path.Combine(Path.GetTempPath(), "ReadingRepair-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try {
            string path = Path.Combine(root, "song.wav");
            using (var w = new BinaryWriter(File.Create(path))) {
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(32036); w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
                w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(32000); w.Write(new byte[32000]);
            }
            const string original = "風が吹き灯火(ともしび)が揺れた";
            var lyrics = new LyricImportService().Parse(original, ".txt", LyricLanguage.Japanese);
            int calls = 0;
            using var http = new HttpClient(new Handler(async req => {
                calls++; if (calls == 1) return Sse("かぜがふき灯火がゆれた");
                using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
                using var input = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
                bool retry = input.RootElement.TryGetProperty("previousFailures", out var prior) && prior.GetArrayLength() > 0;
                if (calls == 2) return Sse("{\"lines\":[{\"lineId\":\"1\",\"reading\":\"かぜがふきとうかともしびがゆれた\",\"uncertain\":true}]}");
                if (retry) Assert.Contains("覆盖", prior[0].GetProperty("validationError").GetString());
                bool omit = missingAnnotation && (!retry || repairFails);
                var segments = new List<ReadingSegment> { new("風が吹き", "かぜがふき"), new("灯火", "ともしび", Reason:null!, Evidence:null!) };
                if (!omit) segments.Add(new("(ともしび)", "", NotSung:true));
                segments.Add(new("が揺れた", "がゆれた"));
                return Sse(JsonSerializer.Serialize(new { lineId="1", originalText="echo need not be trusted", text="", segments }));
            }));
            List<ReadingUpdate> updates = [];
            var log = new AsrDiagnosticLog(Path.Combine(root,"logs"));
            await foreach (var update in new QwenReadingCorrectionService(MediaToolchain.Find().FfmpegPath,http,log).CorrectAsync(path,1,lyrics,new(),new("fake"),
                new(Guid.NewGuid(),"test",true,"https://example.invalid/chat",Model:"test"),new("fake"),new Progress<string>(),default)) updates.Add(update);
            var result=Assert.Single(updates);
            Assert.Equal(missingAnnotation ? (repairFails ? 5 : 4) : 3,calls);
            if (repairFails) { Assert.Null(result.Proposal); Assert.Contains("覆盖",result.Error); }
            else {
                Assert.NotNull(result.Proposal);
                Assert.Equal(original,result.Proposal.Text);
                Assert.Equal("かぜがふきともしびがゆれた",string.Concat(result.Proposal.Segments.Select(x=>x.Reading)));
                Assert.Null(result.Error);
            }
            if (missingAnnotation) Assert.Contains("surface-coverage",File.ReadAllText(log.FilePath));
        } finally { Directory.Delete(root,true); }
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async Task InterruptedStreamIsRecoveredAfterWholePassWithBoundedRetries(int mode)
    {
        string root=Path.Combine(Path.GetTempPath(),"DeferredReading-"+Guid.NewGuid()); Directory.CreateDirectory(root);
        try {
            string audio=Path.Combine(root,"song.wav");
            using(var w=new BinaryWriter(File.Create(audio))) {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));w.Write(32036);w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);
                w.Write((short)1);w.Write((short)1);w.Write(16000);w.Write(32000);w.Write((short)2);w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data"));w.Write(32000);w.Write(new byte[32000]);
            }
            var lyrics=new LyricImportService().Parse(mode==0 ? "空\n夢\n海\n風\n夢\n海" : mode==5 ? "空\n夢\n海\n夢\n夢\n海" : "空\n夢\n海\n風\n星\n月",".txt",LyricLanguage.Japanese);
            using var cancel=new CancellationTokenSource();
            int audioCalls=0,refineCalls=0,batches=0;var retries=new Dictionary<string,int>();
            string Mapping(JsonElement t)=>JsonSerializer.Serialize(new {lineId=t.GetProperty("lineId").GetString(),
                segments=new[]{new ReadingSegment(t.GetProperty("originalText").GetString()!,mode==5 && t.GetProperty("lineId").GetString()=="2" ? "い" : "あ")}});
            HttpResponseMessage Interrupted(string text)=>new(HttpStatusCode.OK) { Content=new StringContent(
                "data: "+JsonSerializer.Serialize(new {choices=new[]{new {delta=new {content=text}}}})+"\n\n",Encoding.UTF8,"text/event-stream") };
            using var http=new HttpClient(new Handler(async req=> {
                using var body=JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
                var messages=body.RootElement.GetProperty("messages");
                if(messages.GetArrayLength()==1) { audioCalls++;return Sse("あ"); }
                using var input=JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
                var targets=input.RootElement.GetProperty("targets").EnumerateArray().ToArray();
                if(body.RootElement.TryGetProperty("response_format",out _)) {
                    refineCalls++;return Sse(JsonSerializer.Serialize(new {lines=targets.Select(t=>new {lineId=t.GetProperty("lineId").GetString(),reading="あ"})}));
                }
                if(targets.Length==3) {
                    batches++;
                    return batches==1 ? Interrupted(Mapping(targets[0])+"\n{\"lineId\":") : Sse(string.Join("\n",targets.Select(Mapping)));
                }
                Assert.Equal(2,batches); // No immediate retry before the rest of the song.
                string original=targets[0].GetProperty("originalText").GetString()!;
                int attempt=retries.GetValueOrDefault(original)+1;retries[original]=attempt;
                Assert.InRange(attempt,1,2);
                if(mode==3) {cancel.Cancel();throw new OperationCanceledException(cancel.Token);}
                if(mode==4) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                if(mode==2 || attempt==1) return Interrupted("{\"lineId\":");
                return Sse(Mapping(targets[0]));
            }));
            var log=new AsrDiagnosticLog(Path.Combine(root,"logs"));var updates=new List<ReadingUpdate>();
            async Task Run() {
                await foreach(var u in new QwenReadingCorrectionService(MediaToolchain.Find().FfmpegPath,http,log).CorrectAsync(audio,1,lyrics,new(),new("fake"),
                    new(Guid.NewGuid(),"test",true,"https://example.invalid/chat",Model:"test"),new("fake"),new Progress<string>(),cancel.Token)) updates.Add(u);
            }
            if(mode==3) await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
            else if(mode==4) await Assert.ThrowsAsync<ReadingServiceHttpException>(Run);
            else await Run();
            Assert.Equal(1,audioCalls);Assert.Equal(2,refineCalls);
            Assert.Equal(updates.Count,updates.Select(u=>u.LineId).Distinct().Count());
            Assert.NotNull(updates.Single(u=>u.LineId==lyrics.Lines[0].Id).Proposal);
            Assert.NotNull(updates.Single(u=>u.LineId==lyrics.Lines[5].Id).Proposal);
            if(mode==0) { Assert.Empty(retries);Assert.Equal(6,updates.Count);Assert.All(updates,u=>Assert.NotNull(u.Proposal));Assert.Contains("duplicate-reuse",File.ReadAllText(log.FilePath)); }
            if(mode is 1 or 2) {
                Assert.Equal(6,updates.Count);Assert.Equal(2,retries.Count);Assert.All(retries,x=>Assert.Equal(2,x.Value));
                Assert.Equal(mode==1 ? 0 : 2,updates.Count(u=>u.Proposal is null));
            }
            if(mode==5) { Assert.Equal(6,updates.Count);Assert.All(updates,u=>Assert.NotNull(u.Proposal));Assert.Single(retries);Assert.Equal(2,retries["夢"]); }
            if(mode is 3 or 4) {Assert.Single(retries);Assert.Equal(1,retries.Values.Single());Assert.Equal(4,updates.Count);}
        } finally {Directory.Delete(root,true);}
    }

    private static HttpResponseMessage Sse(params string[] chunks) => new(HttpStatusCode.OK) { Content = new StringContent(
        string.Concat(chunks.Select(t => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = t } } } }) + "\n\n")) + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
}
