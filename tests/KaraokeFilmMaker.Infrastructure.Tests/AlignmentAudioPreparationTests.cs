using System.Security.Cryptography;
using System.Text.Json;
using KaraokeFilmMaker.Application.Separation;
using KaraokeFilmMaker.Infrastructure.Alignment;
using KaraokeFilmMaker.Infrastructure.Media;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class AlignmentAudioPreparationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "alignment-vocals-" + Guid.NewGuid().ToString("N"));
    private readonly MediaToolchain tools = MediaToolchain.Find();
    public AlignmentAudioPreparationTests() => Directory.CreateDirectory(root);
    private async Task<string> Tone(string name, int frequency, double duration = 2)
    {
        string path = Path.Combine(root, name + ".wav");
        await MediaProcess.RunAsync(tools.FfmpegPath, ["-v", "error", "-f", "lavfi", "-i",
            $"sine=frequency={frequency}:sample_rate=44100:duration={duration.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            "-ac", "2", "-c:a", "pcm_f32le", path]);
        return path;
    }
    private sealed class Fake(Func<SeparationRequest, CancellationToken, Task<string>> run) : IVocalSeparationService
    {
        public Task<string> SeparateAsync(SeparationRequest request, IProgress<SeparationProgress>? progress = null, CancellationToken cancellationToken = default)
            => run(request, cancellationToken);
    }
    [Fact]
    public async Task UsesVocalStemInsteadOfMixPreservesClockAndCleansScratch()
    {
        string original = await Tone("mix", 440), vocal = await Tone("vocal", 880);
        byte[] originalHash = SHA256.HashData(await File.ReadAllBytesAsync(original));
        string job = Path.Combine(root, "job");
        var fake = new Fake((request, token) =>
        {
            Assert.Equal(original, request.SourcePath); Assert.Equal(SeparationOutput.Vocals, request.Output);
            Assert.Equal(SeparationEngine.Gpu, request.Engine); Assert.Equal(2, request.GpuIndex);
            string output = Path.Combine(request.OutputDirectory, "stem.wav"); File.Copy(vocal, output);
            return Task.FromResult(output);
        });
        string prepared = await new AlignmentAudioPreparation(fake, tools).PrepareAsync(original, job, SeparationEngine.Gpu, 2, null, default);
        string expected = Path.Combine(root, "expected.wav");
        await MediaProcess.RunAsync(tools.FfmpegPath, ["-v", "error", "-i", vocal, "-ac", "1", "-ar", "16000", "-c:a", "pcm_f32le", expected]);
        Assert.Equal(await File.ReadAllBytesAsync(expected), await File.ReadAllBytesAsync(prepared));
        using var probe = JsonDocument.Parse(await MediaProcess.RunAsync(tools.FfprobePath,
            ["-v", "error", "-show_streams", "-of", "json", prepared]));
        var stream = probe.RootElement.GetProperty("streams")[0];
        Assert.Equal("16000", stream.GetProperty("sample_rate").GetString());
        Assert.Equal(1, stream.GetProperty("channels").GetInt32());
        Assert.Equal(2d, double.Parse(stream.GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture), 3);
        Assert.Empty(Directory.GetDirectories(job));
        Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(original)));
    }
    [Theory]
    [InlineData("separation")]
    [InlineData("cancel")]
    [InlineData("corrupt")]
    [InlineData("duration")]
    public async Task FailureOrCancellationNeverFallsBackToOriginalAndCleansTemporaryAudio(string reason)
    {
        string original = await Tone("mix", 440), shortVocal = await Tone("short", 880, .5);
        string job = Path.Combine(root, "job");
        using var cancellation = new CancellationTokenSource();
        var fake = new Fake(async (request, token) =>
        {
            string output = Path.Combine(request.OutputDirectory, "partial.wav");
            await File.WriteAllTextAsync(output, "invalid");
            if (reason == "separation") throw new IOException("separation failed");
            if (reason == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            if (reason == "duration") File.Copy(shortVocal, output, true);
            return output;
        });
        var service = new AlignmentAudioPreparation(fake, tools);
        if (reason == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(original, job, SeparationEngine.Cpu, 0, null, cancellation.Token));
        else await Assert.ThrowsAnyAsync<Exception>(() => service.PrepareAsync(original, job, SeparationEngine.Cpu, 0, null, cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(job));
        Assert.True(File.Exists(original));
    }
    public void Dispose() => Directory.Delete(root, true);
}
