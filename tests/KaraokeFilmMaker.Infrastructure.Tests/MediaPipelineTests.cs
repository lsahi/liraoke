using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;
using KaraokeFilmMaker.Infrastructure.Audio;
using KaraokeFilmMaker.Infrastructure.Export;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Persistence;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.Wave;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class MediaPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "KaraokeFilmMaker.MediaTests", Guid.NewGuid().ToString("N"));
    private readonly MediaToolchain _tools = MediaToolchain.Find();

    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(60)]
    public async Task FullMp4Export_HasRequested1080pFrameRateAudioAndBlackGap(int fps)
    {
        KaraokeProject project = await CreateProjectAsync();
        project = project with { ExportPreset = project.ExportPreset with { FramesPerSecond = fps } };
        string output = Path.Combine(_root, "exports", $"result-{fps}.mp4");
        await new VideoExportService(_tools).ExportAsync(_root, project, output);
        MediaDetails details = await new FfprobeMediaReader(_tools).ReadDetailsAsync(output);
        Assert.Equal(1920, details.Width); Assert.Equal(1080, details.Height);
        Assert.Equal(fps, details.FrameRate); Assert.True(details.HasAudio);
        Assert.InRange(details.Duration.TotalSeconds, 1.55, 1.75);
        // Crop the top corner so subtitle pixels cannot hide a black background regression.
        byte[] black = await CornerPixelsAsync(output, 0.65, "gap.png");
        for (var index = 0; index < black.Length; index += 4)
        {
            Assert.True(black[index] < 20 && black[index + 1] < 20 && black[index + 2] < 20);
        }
        byte[] red = await CornerPixelsAsync(output, 0.3, "video.png");
        Assert.True(red[2] > 150 && red[0] < 60);
        Assert.True(File.Exists(output));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.partial.mp4"));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public async Task SlowPlaybackAndWaveformCachesUseOriginalSongWithoutChangingPitch(double rate)
    {
        KaraokeProject project = await CreateProjectAsync();
        var composer = new ProjectAudioComposer(_tools);
        string normal = await composer.PrepareAsync(_root, project);
        string slow = await composer.PrepareAsync(_root, project, rate);
        MediaDetails details = await new FfprobeMediaReader(_tools).ReadDetailsAsync(slow);
        Assert.InRange(details.Duration.TotalSeconds, 1.6 / rate - 0.1, 1.6 / rate + 0.1);
        Assert.InRange(EstimateFrequency(normal), 425, 455);
        Assert.InRange(EstimateFrequency(slow), 425, 455);
        WaveformData waveform = await new WaveformCache().GetAsync(normal, _root);
        Assert.InRange(waveform.Peaks.Length, 155, 165);
        Assert.Contains(waveform.Peaks, value => value > 0);
        Assert.Equal(normal, await composer.PrepareAsync(_root, project));
        var unmuted = project with { VideoClips = project.VideoClips.Select(clip => clip with { IsMuted = false }).ToArray() };
        string mixed = await composer.PrepareAsync(_root, unmuted);
        Assert.NotEqual(normal, mixed);
        Assert.NotEqual(await File.ReadAllBytesAsync(normal), await File.ReadAllBytesAsync(mixed));
    }

    [Fact]
    public async Task ProxyAndThumbnailAreRecreatedFromPortableSource()
    {
        KaraokeProject project = await CreateProjectAsync();
        var cache = new VideoPreviewCache(_tools);
        var files = await cache.PrepareAsync(_root, project.MediaAssets[1]);
        var details = await new FfprobeMediaReader(_tools).ReadDetailsAsync(files.ProxyPath);
        Assert.Equal(960, details.Width); Assert.Equal(540, details.Height); Assert.False(details.HasAudio);
        Assert.Equal(24, details.FrameRate); Assert.True(File.Exists(files.ThumbnailPath));
        Assert.Equal(files, await cache.PrepareAsync(_root, project.MediaAssets[1]));
    }

    [Fact]
    public async Task CanceledExportPreservesExistingOutputAndRemovesIncompleteFiles()
    {
        KaraokeProject project = await CreateProjectAsync();
        string output = Path.Combine(_root, "exports", "existing.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllBytesAsync(output, [1, 2, 3]);
        using var cancel = new CancellationTokenSource();
        var progress = new ImmediateProgress(value => { if (value.Fraction >= 0.1) { cancel.Cancel(); } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new VideoExportService(_tools).ExportAsync(_root, project, output, progress, cancel.Token));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(output));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.partial.mp4"));
    }

    [Fact]
    public async Task RecoveryDoesNotOverwriteExplicitSaveAndWorkspaceCanMove()
    {
        KaraokeProject project = await CreateProjectAsync();
        var repository = new JsonProjectRepository();
        await repository.SaveAsync(_root, project);
        var store = new ProjectRecoveryStore();
        await store.SaveAsync(_root, project with { Title = "Recovered" });
        Assert.Equal(project.Title, (await repository.LoadAsync(_root)).Title);
        Assert.Equal("Recovered", (await store.FindAsync(_root, project))!.Title);
        KaraokeProject roundTrip = await repository.LoadAsync(_root);
        Assert.Equal(project.VideoClips[0].Duration, roundTrip.VideoClips[0].Duration);
        Assert.Equal(project.Lyrics.Lines[0].Timing, roundTrip.Lyrics.Lines[0].Timing);
        Assert.Equal(project.Lyrics.Lines[0].Units[0].DisplayRanges[0], roundTrip.Lyrics.Lines[0].Units[0].DisplayRanges[0]);
        string relocated = _root + "-moved";
        Directory.Move(_root, relocated);
        try
        {
            KaraokeProject reopened = await repository.LoadAsync(relocated);
            Assert.True(File.Exists(Path.Combine(relocated, reopened.MediaAssets[0].RelativePath)));
            Assert.Equal("Recovered", (await store.FindAsync(relocated, reopened))!.Title);
        }
        finally { Directory.Move(relocated, _root); }
    }

    [Fact]
    public async Task SingleTrackExportChangesVideoAtCutsAndPreservesThreeLaneProject()
    {
        KaraokeProject project = await CreateProjectAsync();
        string upperFile = Path.Combine(_root, "media", "video", "upper.mp4");
        await MediaProcess.RunAsync(_tools.FfmpegPath, ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i",
            "color=c=blue:s=320x180:r=30:d=1.6", "-c:v", "libx264", "-pix_fmt", "yuv420p", upperFile]);
        var lower = project.MediaAssets[1];
        var upper = lower with { Id = Guid.NewGuid(), DisplayName = "upper.mp4", RelativePath = "media/video/upper.mp4" };
        project = project with
        {
            MediaAssets = [.. project.MediaAssets, upper],
            // Deliberately use non-chronological storage order; rendering follows the single timeline.
            VideoClips = [new VideoClip(Guid.NewGuid(), upper.Id, ProjectTime.FromSeconds(0.4), ProjectTime.Zero, ProjectDuration.FromSeconds(0.3), true),
                new VideoClip(Guid.NewGuid(), lower.Id, ProjectTime.Zero, ProjectTime.Zero, ProjectDuration.FromSeconds(0.4), true),
                new VideoClip(Guid.NewGuid(), lower.Id, ProjectTime.FromSeconds(0.7), ProjectTime.FromSeconds(0.7), ProjectDuration.FromSeconds(0.5), true)],
        };
        string output = Path.Combine(_root, "exports", "tracks.mp4");
        await new VideoExportService(_tools).ExportAsync(_root, project, output);
        byte[] above = await CornerPixelsAsync(output, 0.5, "above.png");
        Assert.True(above[0] > 150 && above[2] < 60);
        byte[] fallback = await CornerPixelsAsync(output, 0.9, "below.png");
        Assert.True(fallback[2] > 150 && fallback[0] < 60);
        var repository = new JsonProjectRepository();
        await repository.SaveAsync(_root, project);
        var loaded = await repository.LoadAsync(_root);
        Assert.Equal(1, loaded.VideoTrackCount);
        Assert.All(loaded.VideoClips, clip => Assert.Equal(0, clip.TrackIndex));
    }

    [Fact]
    public async Task SplittingSongVideoDoesNotDoubleItsMasterAudio()
    {
        KaraokeProject project = await CreateProjectAsync();
        var video = project.MediaAssets[1];
        var clip = new VideoClip(Guid.NewGuid(), video.Id, ProjectTime.Zero, ProjectTime.Zero, video.Duration, false);
        project = project with { MasterAudioAssetId = video.Id, VideoClips = [clip] };
        var composer = new ProjectAudioComposer(_tools);
        string before = await composer.PrepareAsync(_root, project);
        var split = new KaraokeFilmMaker.Application.Timeline.VideoTimelineService().SplitClip(project, clip.Id, ProjectTime.FromSeconds(0.8));
        string after = await composer.PrepareAsync(_root, split);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task OldSingleTrackProjectOpensAsNewSchemaWithoutLosingTimings()
    {
        KaraokeProject project = await CreateProjectAsync();
        var repository = new JsonProjectRepository();
        await repository.SaveAsync(_root, project with { SchemaVersion = 1 });
        string path = Path.Combine(_root, "project.kfp");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json.AsObject().Remove("videoTrackCount");
        foreach (var clip in json["videoClips"]!.AsArray()) { clip!.AsObject().Remove("trackIndex"); }
        await File.WriteAllTextAsync(path, json.ToJsonString());
        var reopened = await repository.LoadAsync(_root);
        Assert.Equal(KaraokeProject.CurrentSchemaVersion, reopened.SchemaVersion);
        Assert.All(reopened.VideoClips, clip => Assert.Equal(0, clip.TrackIndex));
        Assert.Equal(project.VideoClips[1].TimelineStart, reopened.VideoClips[1].TimelineStart);
        Assert.Equal(project.VideoClips[1].SourceStart, reopened.VideoClips[1].SourceStart);
    }

    private async Task<KaraokeProject> CreateProjectAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "media", "audio"));
        Directory.CreateDirectory(Path.Combine(_root, "media", "video"));
        string audio = Path.Combine(_root, "media", "audio", "song.wav");
        string video = Path.Combine(_root, "media", "video", "background.mp4");
        await MediaProcess.RunAsync(_tools.FfmpegPath,
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=1.6", "-ar", "48000", "-ac", "2", audio]);
        await MediaProcess.RunAsync(_tools.FfmpegPath,
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=320x180:r=30:d=1.6", "-f", "lavfi", "-i", "sine=frequency=880:duration=1.6",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", video]);
        var master = new MediaAsset(Guid.NewGuid(), "song.wav", "media/audio/song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(1.6));
        var background = new MediaAsset(Guid.NewGuid(), "background.mp4", "media/video/background.mp4", MediaKind.Video, ProjectDuration.FromSeconds(1.6));
        var lyrics = new LyricImportService().Parse("[00:00.00]未来へ進む", ".lrc", LyricLanguage.Japanese);
        lyrics = new LyricTimingService().ApplyLineMarkers(lyrics, [ProjectTime.Zero], ProjectTime.FromSeconds(1.6));
        return KaraokeProject.Create("Pipeline", DateTimeOffset.UtcNow) with
        {
            MasterAudioAssetId = master.Id, MediaAssets = [master, background], Lyrics = lyrics,
            VideoClips = [new VideoClip(Guid.NewGuid(), background.Id, ProjectTime.FromSeconds(0.2), ProjectTime.Zero, ProjectDuration.FromSeconds(0.3), true),
                new VideoClip(Guid.NewGuid(), background.Id, ProjectTime.FromSeconds(0.8), ProjectTime.FromSeconds(0.2), ProjectDuration.FromSeconds(0.4), true)],
        };
    }

    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }

    private async Task<byte[]> CornerPixelsAsync(string video, double seconds, string name)
    {
        string image = Path.Combine(_root, name);
        await MediaProcess.RunAsync(_tools.FfmpegPath, ["-nostdin", "-v", "error", "-y", "-ss", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", video, "-frames:v", "1", "-vf", "crop=100:100:0:0", image]);
        using FileStream stream = File.OpenRead(image);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[100 * 100 * 4];
        bitmap.CopyPixels(pixels, 100 * 4, 0);
        return pixels;
    }

    private static double EstimateFrequency(string path)
    {
        using var reader = new WaveFileReader(path);
        var source = reader.ToSampleProvider();
        float[] samples = new float[48000 * 2];
        int count = source.Read(samples);
        int crossings = 0;
        for (int index = 4800 * 2 + 2; index < count; index += 2)
        {
            if (samples[index - 2] <= 0 && samples[index] > 0) { crossings++; }
        }
        return crossings / ((count / 2d - 4800) / 48000);
    }

    private sealed class ImmediateProgress(Action<ExportProgress> action) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => action(value);
    }
}
