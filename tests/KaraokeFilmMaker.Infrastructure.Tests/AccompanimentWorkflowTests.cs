using System.Text.Json;
using System.Text.Json.Nodes;
using KaraokeFilmMaker.Application.Media;
using KaraokeFilmMaker.Application.Separation;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Validation;
using KaraokeFilmMaker.Infrastructure.Audio;
using KaraokeFilmMaker.Infrastructure.Export;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Infrastructure.Separation;
using NAudio.Wave;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class AccompanimentWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Liraoke.Accompaniment", Guid.NewGuid().ToString("N"));
    private readonly MediaToolchain _tools = MediaToolchain.Find();

    private KaraokeProject Song()
    {
        Directory.CreateDirectory(_root);
        WriteTone(Path.Combine(_root, "song.wav"), 440);
        WriteTone(Path.Combine(_root, "generated.wav"), 880);
        var asset = new MediaAsset(Guid.NewGuid(), "原曲", "song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(2), "original-hash");
        return KaraokeProject.Create("映射", DateTimeOffset.UtcNow) with
        { MediaAssets = [asset], MasterAudioAssetId = asset.Id, ExportPreset = new(320, 180, 24, 1000, 192),
            WorkArea = new(ProjectTime.FromSeconds(0.5), ProjectTime.FromSeconds(1.5)) };
    }

    private Task<KaraokeProject> Register(KaraokeProject project) => new AccompanimentStore(_tools).RegisterAsync(
        _root, project, project.MasterAudioAssetId!.Value, Path.Combine(_root, "generated.wav"));

    [Fact]
    public async Task RegistrationPreservesSourceAndAllEditsAndSurvivesPortableReopen()
    {
        var original = Song(); var project = await Register(original);
        Assert.Same(original.MediaAssets, project.MediaAssets); Assert.Same(original.Lyrics, project.Lyrics);
        Assert.Same(original.VideoClips, project.VideoClips); Assert.Equal(original.MasterAudioAssetId, project.MasterAudioAssetId);
        Assert.Equal(original.WorkArea, project.WorkArea); Assert.Equal(original.Metronome, project.Metronome);
        var mapping = Assert.Single(project.Accompaniments); Assert.True(AccompanimentStore.IsAvailable(_root, project));
        var repo = new JsonProjectRepository(); await repo.SaveAsync(_root, project);
        var reopened = await repo.LoadAsync(_root); Assert.Equal(mapping, Assert.Single(reopened.Accompaniments));
        string moved = _root + "-moved";
        Directory.Move(_root, moved);
        try { Assert.True(AccompanimentStore.IsAvailable(moved, await repo.LoadAsync(moved))); }
        finally { Directory.Move(moved, _root); }
        var replaced = await Register(project);
        Assert.NotEqual(mapping.RelativePath, Assert.Single(replaced.Accompaniments).RelativePath);
        Assert.True(File.Exists(AccompanimentStore.Resolve(_root, mapping))); // Undo keeps older audio.
        var removed = MediaAssetRemoval.Remove(project, project.MasterAudioAssetId!.Value, true);
        Assert.Empty(removed.Accompaniments); Assert.Empty(ProjectValidator.Validate(removed));
        Assert.True(AccompanimentStore.IsAvailable(_root, project));
        File.Delete(AccompanimentStore.Resolve(_root, mapping));
        Assert.False(AccompanimentStore.IsAvailable(_root, project));
        await Assert.ThrowsAsync<FileNotFoundException>(() => new ProjectAudioComposer(_tools).PrepareAsync(_root, project, accompaniment: true));
    }

    [Fact]
    public async Task LegacySchemaEightLoadsWithoutMappingsAndNewSchemaRoundTrips()
    {
        var project = Song(); var repo = new JsonProjectRepository(); await repo.SaveAsync(_root, project);
        string path = Directory.GetFiles(_root, "*.lrn").FirstOrDefault() ?? Path.Combine(_root, "project.kfp");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        json["schemaVersion"] = 8; json.Remove("accompaniments"); await File.WriteAllTextAsync(path, json.ToJsonString());
        var migrated = await repo.LoadAsync(_root);
        Assert.Equal(KaraokeProject.CurrentSchemaVersion, migrated.SchemaVersion); Assert.Empty(migrated.Accompaniments);
        Assert.Equal(project.MasterAudioAssetId, migrated.MasterAudioAssetId); Assert.Equal(project.WorkArea, migrated.WorkArea);
        json["schemaVersion"] = KaraokeProject.CurrentSchemaVersion + 1; await File.WriteAllTextAsync(path, json.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => repo.LoadAsync(_root));
    }

    [Theory]
    [InlineData("../outside.wav")]
    [InlineData("accompaniments/../outside.wav")]
    [InlineData("C:/outside.wav")]
    [InlineData("accompaniments/evil:stream.wav")]
    public void InvalidMappingPathsAreRejected(string path)
    {
        var project = Song(); var asset = project.MediaAssets[0];
        project = project with { Accompaniments = [new(asset.Id, path, asset.Duration, asset.ContentHash, DateTimeOffset.UtcNow)] };
        Assert.NotEmpty(ProjectValidator.Validate(project));
    }

    [Fact]
    public async Task WrongDurationCancellationDuplicateAndChangedSourceNeverBecomeAvailable()
    {
        var project = Song();
        WriteTone(Path.Combine(_root, "short.wav"), 880, 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => new AccompanimentStore(_tools).RegisterAsync(
            _root, project, project.MasterAudioAssetId!.Value, Path.Combine(_root, "short.wav")));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AccompanimentStore(_tools).RegisterAsync(
            _root, project, project.MasterAudioAssetId!.Value, Path.Combine(_root, "generated.wav"), cts.Token));
        Assert.Empty(project.Accompaniments);
        var registered = await Register(project); var item = registered.Accompaniments[0];
        Assert.NotEmpty(ProjectValidator.Validate(registered with { Accompaniments = [item, item] }));
        var changed = registered with { MediaAssets = [project.MediaAssets[0] with { ContentHash = "changed" }] };
        Assert.False(AccompanimentStore.IsAvailable(_root, changed)); Assert.NotEmpty(ProjectValidator.Validate(changed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealPreviewAndCroppedExportsSelectCorrectAudioIncludingOriginalVideoClips(bool video)
    {
        var original = Song();
        if (video)
        {
            await MediaProcess.RunAsync(_tools.FfmpegPath, ["-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=320x180:d=2:r=24",
                "-i", Path.Combine(_root, "song.wav"), "-c:v", "libx264", "-c:a", "aac", "-shortest", Path.Combine(_root, "song.mp4")]);
            var asset = original.MediaAssets[0] with { RelativePath = "song.mp4", Kind = MediaKind.Video };
            original = original with { MediaAssets = [asset], VideoClips = [new(Guid.NewGuid(), asset.Id,
                ProjectTime.FromSeconds(0.25), ProjectTime.Zero, ProjectDuration.FromSeconds(1.5), false)] };
        }
        var project = await Register(original);
        var composer = new ProjectAudioComposer(_tools);
        string on = await composer.PrepareAsync(_root, project);
        string off = await composer.PrepareAsync(_root, project, accompaniment: true);
        Assert.NotEqual(on, off); AssertTone(on, 440, 880); AssertTone(off, 880, 440);
        Assert.Equal(off, await composer.PrepareAsync(_root, project, accompaniment: true));
        string slow = await composer.PrepareAsync(_root, project, 0.5, accompaniment: true);
        Assert.InRange((await new FfprobeMediaReader(_tools).ReadDetailsAsync(slow)).Duration.TotalSeconds, 3.8, 4.1);
        foreach (var version in AudioExportVersions.Create(Path.Combine(_root, "Result.mp4"), true, true))
        {
            await new VideoExportService(_tools).ExportAsync(_root, project, version.Path, accompaniment: version.Accompaniment);
            Assert.InRange((await new FfprobeMediaReader(_tools).ReadDetailsAsync(version.Path)).Duration.TotalSeconds, 0.99, 1.1);
            string decoded = version.Path + ".wav";
            await MediaProcess.RunAsync(_tools.FfmpegPath, ["-v", "error", "-y", "-i", version.Path, "-vn", "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "1", decoded]);
            AssertTone(decoded, version.Accompaniment ? 880 : 440, version.Accompaniment ? 440 : 880);
        }
        Assert.Empty(Directory.GetFiles(_root, "*.partial.mp4"));
        Assert.Equal(original.MediaAssets, project.MediaAssets); Assert.Equal(original.VideoClips, project.VideoClips);
    }

    private static void WriteTone(string path, int frequency, int seconds = 2)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 1));
        for (int i = 0; i < seconds * 48000; i++) writer.WriteSample((float)(0.3 * Math.Sin(2 * Math.PI * frequency * i / 48000)));
    }

    [Fact]
    public async Task CancelSecondVersionPreservesFirstAndExistingDestination()
    {
        var project = await Register(Song());
        var versions = AudioExportVersions.Create(Path.Combine(_root, "both.mp4"), true, true);
        var exporter = new VideoExportService(_tools);
        await exporter.ExportAsync(_root, project, versions[0].Path);
        byte[] first = await File.ReadAllBytesAsync(versions[0].Path);
        await File.WriteAllTextAsync(versions[1].Path, "previous export");
        using var cts = new CancellationTokenSource();
        var progress = new CancelProgress(cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(_root, project,
            versions[1].Path, progress, cts.Token, accompaniment: true));
        Assert.Equal(first, await File.ReadAllBytesAsync(versions[0].Path));
        Assert.Equal("previous export", await File.ReadAllTextAsync(versions[1].Path));
        Assert.Empty(Directory.GetFiles(_root, "*.partial.mp4"));
    }

    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<ExportProgress>
    { public void Report(ExportProgress value) { if (value.Fraction >= 0.05) cancellation.Cancel(); } }
    private static void AssertTone(string path, int expected, int absent)
    {
        using var reader = new WaveFileReader(path); reader.CurrentTime = TimeSpan.FromSeconds(0.1);
        int channels = reader.WaveFormat.Channels, rate = reader.WaveFormat.SampleRate;
        float[] samples = new float[rate / 5 * channels]; int count = reader.ToSampleProvider().Read(samples) / channels;
        double Energy(int hz)
        {
            double re = 0, im = 0;
            for (int i = 0; i < count; i++) { double angle = 2 * Math.PI * hz * i / rate; re += samples[i * channels] * Math.Cos(angle); im += samples[i * channels] * Math.Sin(angle); }
            return re * re + im * im;
        }
        Assert.True(Energy(expected) > Energy(absent) * 50 + 1, $"Expected {expected} Hz, not {absent} Hz in {path}");
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
