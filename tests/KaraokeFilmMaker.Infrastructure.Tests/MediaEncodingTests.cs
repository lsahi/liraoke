using System.Text;
using System.Text.Json;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Infrastructure.Audio;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Infrastructure.Workspaces;

namespace KaraokeFilmMaker.Infrastructure.Tests;

// Console.OutputEncoding is process-global. Never change it alongside another test collection.
[CollectionDefinition("Media console encoding", DisableParallelization = true)]
public sealed class MediaConsoleEncodingCollection;

[Collection("Media console encoding")]
public sealed class MediaEncodingTests
{
    [Theory]
    [InlineData(936)]
    [InlineData(932)]
    [InlineData(1252)]
    [InlineData(65001)]
    public async Task UnicodeMetadataImportAndAudioPreparationIgnoreConsoleCodePage(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding originalEncoding = Console.OutputEncoding;
        string root = Path.Combine(Path.GetTempPath(), "Liraoke.MediaEncoding", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string artist = "宝鐘マリン";
        const string title = "测试・日本語☆ \"quoted\" \\ path\r\nsecond line";
        var tools = MediaToolchain.Find();
        string source = Path.Combine(root, "日本語・宝鐘マリン☆.mp3");
        try
        {
            Console.OutputEncoding = Encoding.GetEncoding(codePage);
            await MediaProcess.RunAsync(tools.FfmpegPath,
                ["-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "1",
                    "-c:a", "libmp3lame", "-metadata", "artist=" + artist, "-metadata", "title=" + title, source]);
            List<string> lines = [];
            string json = await MediaProcess.RunAsync(tools.FfprobePath,
                ["-v", "error", "-show_format", "-show_streams", "-of", "json", source], outputLine: lines.Add);
            using var parsed = JsonDocument.Parse(json);
            var tags = parsed.RootElement.GetProperty("format").GetProperty("tags");
            Assert.Equal(artist, tags.GetProperty("artist").GetString());
            Assert.Equal(title, tags.GetProperty("title").GetString());
            Assert.Contains(lines, line => line.Contains(artist));
            var workspace = new WorkspaceService(new JsonProjectRepository(), new FfprobeMediaReader(tools));
            var session = await workspace.CreateAsync(Path.Combine(root, "workspace"), "编码测试");
            session = await workspace.ImportMediaAsync(session, source, MediaKind.Audio);
            var asset = Assert.Single(session.Project.MediaAssets);
            Assert.Equal(Path.GetFileName(source), asset.DisplayName);
            Assert.InRange(asset.Duration.TotalSeconds, 1, 1.1);
            Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(Path.Combine(session.RootPath, asset.RelativePath)));
            session = await workspace.SaveAsync(session);
            var reopened = await workspace.OpenAsync(session.RootPath);
            Assert.Equal(asset, Assert.Single(reopened.Project.MediaAssets));
            string wave = await new ProjectAudioComposer(tools).PrepareAsync(reopened.RootPath, reopened.Project);
            Assert.True(File.Exists(wave));
            // MP3 encoder padding and the tempo filter may differ by a few tens of milliseconds.
            Assert.InRange((await new FfprobeMediaReader(tools).ReadDetailsAsync(wave)).Duration.TotalSeconds, 0.9, 1.1);
            string missing = Path.Combine(root, "不存在・宝鐘マリン.mp3");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => MediaProcess.RunAsync(tools.FfprobePath,
                ["-v", "error", "-show_format", missing]));
            Assert.Contains("不存在・宝鐘マリン.mp3", error.Message);
        }
        finally
        {
            Console.OutputEncoding = originalEncoding;
            Directory.Delete(root, true);
        }
    }
}
