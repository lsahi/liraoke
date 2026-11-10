using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using KaraokeFilmMaker.Infrastructure.Ai;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class ReadingStreamTests
{
    [Fact]
    public async Task LongReasoningAndPacketEnvelopesDoNotConsumeAnswerBudget()
    {
        string reasoning = Packet(new { reasoning_content = "private-reasoning" });
        string response = string.Concat(Enumerable.Repeat(reasoning, 7000)) + Packet(new { content = "なさ" }) + "data: [DONE]\n\n";
        Assert.True(response.Length > 1_000_000);
        string root = Path.Combine(Path.GetTempPath(), "ReadingStream-" + Guid.NewGuid());
        try {
            var log = new AsrDiagnosticLog(root);
            Assert.Equal("なさ", await Read(response, log));
            var rows = File.ReadAllLines(log.FilePath).Select(l => JsonSerializer.Deserialize<JsonElement>(l)).ToArray();
            Assert.Equal(2, rows.Single(r => r.GetProperty("outcome").GetString() == "answer-characters").GetProperty("count").GetInt32());
            Assert.True(rows.Single(r => r.GetProperty("outcome").GetString() == "reasoning-characters").GetProperty("count").GetInt32() > 0);
            Assert.DoesNotContain("private-reasoning", File.ReadAllText(log.FilePath));
        } finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("length", ReadingFailureCode.Truncated)]
    [InlineData("content_filter", ReadingFailureCode.Filtered)]
    [InlineData("tool_calls", ReadingFailureCode.UnexpectedFinish)]
    public async Task FinishReasonsRemainDistinct(string reason, ReadingFailureCode expected)
    {
        var error = await Assert.ThrowsAsync<ReadingServiceFailure>(() => Read(Packet(new { content = "" }, reason)));
        Assert.Equal(expected, error.Code);
        Assert.Equal(error.Message, QwenReadingCorrectionService.DescribeFailure(error));
    }

    [Fact]
    public async Task PrematureEofIsNotConfusedWithEmptyOrOverlongOutput()
    {
        var error = await Assert.ThrowsAsync<ReadingServiceFailure>(() => Read(Packet(new { content = "partial" })));
        Assert.Equal(ReadingFailureCode.IncompleteStream, error.Code);
    }

    [Fact]
    public async Task FinalAnswerStillHasItsOwnLimit()
    {
        string packet = Packet(new { content = new string('a', 600000) });
        var error = await Assert.ThrowsAsync<ReadingServiceFailure>(() => Read(packet + packet + "data: [DONE]\n\n"));
        Assert.Equal(ReadingFailureCode.ContentLimit, error.Code);
    }

    [Fact]
    public async Task OversizedEventHasItsOwnFailureCode()
    {
        var error = await Assert.ThrowsAsync<ReadingServiceFailure>(() => Read(Packet(new { content = new string('a', 1000001) })));
        Assert.Equal(ReadingFailureCode.EventTooLarge, error.Code);
    }

    [Fact]
    public void TranscriptIsCompleteReadableAndDoesNotDisableMetadataLogging()
    {
        string root = Path.Combine(Path.GetTempPath(), "TranscriptLog-" + Guid.NewGuid());
        try {
            var log = new AsrDiagnosticLog(root);
            string raw = "星空（なさ）\n\"未完\"" + new string('あ', 700000);
            log.Transcript(2, 58, 118, raw, false);
            log.Record("workflow", "completed");
            var lines = File.ReadAllLines(log.FilePath);
            using var doc = JsonDocument.Parse(lines[1]);
            Assert.Equal(raw, doc.RootElement.GetProperty("text").GetString());
            Assert.Equal("partial", doc.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(58, doc.RootElement.GetProperty("startSeconds").GetDouble());
            Assert.Equal(118, doc.RootElement.GetProperty("endSeconds").GetDouble());
            Assert.Contains("星空", lines[1]);
            Assert.Contains("completed", lines[^1]);
            Assert.True(log.IsAvailable);
        } finally { Directory.Delete(root, true); }
    }

    [Theory] [InlineData("")] [InlineData("   ")]
    public async Task ReasoningOnlyOrWhitespaceIsClassifiedImmediately(string answer)
    {
        var error = await Assert.ThrowsAsync<ReadingServiceFailure>(() => Read(Packet(new { reasoning_content = "thought", content = answer }, "stop") + "data: malformed trailing data\n"));
        Assert.Equal(ReadingFailureCode.EmptyAnswer, error.Code);
    }
    [Fact] public async Task StopFinishesWithoutWaitingForDoneOrParsingTrailingPackets()
    {
        Assert.Equal("すみ", await Read(Packet(new { content = "すみ" }, "stop") + "data: malformed trailing data\n"));
    }

    private static string Packet(object delta, string? finish = null) => "data: " + JsonSerializer.Serialize(new {
        id = "offline-test-packet-identifier", @object = "chat.completion.chunk", model = "test-model",
        choices = new[] { new { index = 0, delta, finish_reason = finish } }
    }) + "\n\n";

    private static async Task<string> Read(string response, AsrDiagnosticLog? log = null)
    {
        using var client = new HttpClient(new Handler(response));
        var service = new QwenReadingCorrectionService("unused", client, log);
        var text = new StringBuilder();
        await foreach (var delta in service.TranscribeAsync([1], new(), new("fake"), default)) text.Append(delta);
        return text.ToString();
    }
    private sealed class Handler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "text/event-stream") });
    }
}
