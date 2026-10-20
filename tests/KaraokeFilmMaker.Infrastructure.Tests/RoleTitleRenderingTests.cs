using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Rendering;
using KaraokeFilmMaker.Infrastructure.Export;
using KaraokeFilmMaker.Infrastructure.Media;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class RoleTitleRenderingTests
{
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(960, 720)]
    [InlineData(720, 720)]
    [InlineData(720, 1280)]
    public async Task ChorusIsOneCopyWithRedUpperBlueLowerAndWordOverride(int width, int height)
    {
        var p = Project();
        var line = p.Lyrics.Lines[0] with { RoleIds = p.Roles.Select(r => r.Id).ToArray() };
        p = p with { Lyrics = p.Lyrics with { Lines = [line] } };
        await using var renderer = new WpfSubtitleFrameRenderer(p);
        var frame = await renderer.RenderAsync(ProjectTime.FromSeconds(3.9), width, height);
        var red = Pixels(frame, true); var blue = Pixels(frame, false);
        Assert.True(red.Count > 20); Assert.True(blue.Count > 20);
        Assert.True(red.Average(v => v.Y) < blue.Average(v => v.Y));
        Assert.InRange(Math.Abs(red.Min(v => v.X) - blue.Min(v => v.X)), 0, 20);
        await using var single = new WpfSubtitleFrameRenderer(p with { Lyrics = p.Lyrics with { Lines = [line with { RoleIds = [p.Roles[0].Id] }] } });
        var singleFrame = await single.RenderAsync(ProjectTime.FromSeconds(3.9), width, height);
        // Color bands cannot add a duplicate row or change the occupied alpha mask.
        Assert.Equal(Alpha(singleFrame), Alpha(frame));
        var split = line with { RoleIds = [], Units = line.Units.Select((u, i) => u with { RoleIds = [p.Roles[i].Id] }).ToArray() };
        await using var separate = new WpfSubtitleFrameRenderer(p with { Lyrics = p.Lyrics with { Lines = [split] } });
        var words = await separate.RenderAsync(ProjectTime.FromSeconds(3.9), width, height);
        Assert.True(Pixels(words, true).Max(v => v.X) < Pixels(words, false).Min(v => v.X));
        Snapshot(frame, $"chorus-{width}x{height}.png"); Snapshot(words, $"word-roles-{width}x{height}.png");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JapaneseRubyUsesSameChorusBandsAndFollowsPrimarySweep(bool romanized)
    {
        var p = Project();
        var line = p.Lyrics.Lines[0] with { PrimaryText = "思い出", AnnotationType = AnnotationType.Kana,
            Units = [new(Guid.NewGuid(), [new(0, 1)], "おも", "omo", new(ProjectTime.Zero, ProjectTime.FromSeconds(1)), null, true),
                new(Guid.NewGuid(), [new(1, 1)], "い", "i", new(ProjectTime.FromSeconds(1), ProjectTime.FromSeconds(2)), null, true),
                new(Guid.NewGuid(), [new(2, 1)], "で", "de", new(ProjectTime.FromSeconds(2), ProjectTime.FromSeconds(3)), null, true)],
            RoleIds = p.Roles.Select(r => r.Id).ToArray() };
        line = line with { Presentation = LanguagePresentation.BuildJapanese(line) };
        p = p with { Roles = p.Roles.Select(r => r with { Style = r.Style with { InactiveColor = "#FFFFFFFF", ShowRomanization = romanized } }).ToArray(),
            Lyrics = new(LyricLanguage.Japanese, [line]) };
        await using var renderer = new WpfSubtitleFrameRenderer(p);
        var frame = await renderer.RenderAsync(ProjectTime.FromSeconds(3.5), 1280, 720);
        Assert.True(Pixels(frame, true).Count > 30); Assert.True(Pixels(frame, false).Count > 30);
        var before = await renderer.RenderAsync(ProjectTime.FromSeconds(1), 1280, 720);
        Assert.True(Pixels(before, true).Max(v => v.X) < Pixels(frame, true).Max(v => v.X) - 10);
        var seek = await renderer.RenderAsync(ProjectTime.FromSeconds(3.5), 1280, 720);
        Assert.Equal(frame.BgraPixels.ToArray(), seek.BgraPixels.ToArray());
        Snapshot(frame, $"chorus-japanese-{romanized}.png");
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(960, 720)]
    [InlineData(720, 720)]
    [InlineData(720, 1280)]
    public async Task IndependentTitleSupportsDefaultFilenameCornersAndDisabled(int width, int height)
    {
        var p = Project();
        var media = new MediaAsset(Guid.NewGuid(), "海の歌.mp3", "media/song.mp3", MediaKind.Audio, ProjectDuration.FromSeconds(5));
        p = p with { Lyrics = new(LyricLanguage.Japanese, []), MasterAudioAssetId = media.Id, MediaAssets = [media],
            SongTitle = new() { Enabled = true, Color = "#FFFF0000", OutlineWidth = 0, ShadowOffset = 0 } };
        Assert.Equal("海の歌", p.SongTitle.ResolveText(p));
        await using var leftRenderer = new WpfSubtitleFrameRenderer(p);
        var left = await leftRenderer.RenderAsync(ProjectTime.Zero, width, height);
        await using var rightRenderer = new WpfSubtitleFrameRenderer(p with { SongTitle = p.SongTitle with { Position = SongTitlePosition.TopRight } });
        var right = await rightRenderer.RenderAsync(ProjectTime.Zero, width, height);
        Assert.True(Pixels(left, true).Max(v => v.Y) < height / 3);
        Assert.True(Pixels(left, true).Max(v => v.X) < width / 2);
        Assert.True(Pixels(right, true).Min(v => v.X) > width / 2);
        await using var disabled = new WpfSubtitleFrameRenderer(p with { SongTitle = p.SongTitle with { Enabled = false } });
        Assert.All(Alpha(await disabled.RenderAsync(ProjectTime.Zero, width, height)), a => Assert.Equal(0, a));
        Snapshot(left, $"title-left-{width}x{height}.png"); Snapshot(right, $"title-right-{width}x{height}.png");
    }

    [Fact]
    public async Task SharedJapaneseWordCanSwitchFromSplitKanaRubyToWholeGroupRomaji()
    {
        var p = Project();
        var readings = new[] { "お", "も", "い", "で" };
        var romanizations = new[] { "o", "mo", "i", "de" };
        var line = p.Lyrics.Lines[0] with { PrimaryText = "思い出", RoleIds = p.Roles.Select(r => r.Id).ToArray(),
            Units = readings.Select((reading, i) => new LyricUnit(Guid.NewGuid(), [new(0, 3)], reading, romanizations[i],
                new(ProjectTime.FromSeconds(i), ProjectTime.FromSeconds(i + 1)), null, true)).ToArray() };
        line = line with { Presentation = LanguagePresentation.BuildJapanese(line) };
        Assert.Equal(3, line.Presentation.SubtitleRuns.Count);
        Assert.Single(line.Presentation.RomanizedSubtitleRuns!);
        p = p with { Lyrics = new(LyricLanguage.Japanese, [line]) };
        await using var kana = new WpfSubtitleFrameRenderer(p);
        await using var romaji = new WpfSubtitleFrameRenderer(p with { Roles = p.Roles.Select(r => r with
        { Style = r.Style with { ShowRomanization = true } }).ToArray() });
        var kanaFrame = await kana.RenderAsync(ProjectTime.FromSeconds(4.5), 1280, 720);
        var romajiFrame = await romaji.RenderAsync(ProjectTime.FromSeconds(4.5), 1280, 720);
        Assert.NotEqual(kanaFrame.BgraPixels.ToArray(), romajiFrame.BgraPixels.ToArray());
        Snapshot(romajiFrame, "chorus-shared-word-romaji.png");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealMp4ContainsTitleAndOptionalRoleLyrics(bool withRoles)
    {
        string root = Path.Combine(Path.GetTempPath(), "Liraoke.TitleExport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tools = MediaToolchain.Find();
            await MediaProcess.RunAsync(tools.FfmpegPath, ["-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=22050:cl=mono", "-t", "0.5", Path.Combine(root, "song.wav")]);
            var asset = new MediaAsset(Guid.NewGuid(), "Song.wav", "song.wav", MediaKind.Audio, ProjectDuration.FromSeconds(.5));
            var p = KaraokeProject.Create("Title export", DateTimeOffset.UnixEpoch) with { MasterAudioAssetId = asset.Id, MediaAssets = [asset],
                SongTitle = new() { Enabled = true, Text = "TITLE", Color = "#FFFF0000", OutlineWidth = 0 }, ExportPreset = new(1280, 720, 24, 4000, 192) };
            if (withRoles)
            {
                var duet = Project();
                p = p with { Roles = duet.Roles, Lyrics = duet.Lyrics with
                { Lines = [duet.Lyrics.Lines[0] with { RoleIds = duet.Roles.Select(r => r.Id).ToArray() }] } };
            }
            string output = Path.Combine(root, "title.mp4"), image = Path.Combine(root, "frame.png");
            await new VideoExportService(tools).ExportAsync(root, p, output);
            var details = await new FfprobeMediaReader(tools).ReadDetailsAsync(output);
            Assert.InRange(details.Duration.TotalSeconds, .45, .65); Assert.True(details.HasAudio);
            await MediaProcess.RunAsync(tools.FfmpegPath, ["-v", "error", "-y", "-i", output, "-frames:v", "1", "-update", "1", image]);
            using var stream = File.OpenRead(image);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            byte[] data = new byte[1280 * 720 * 4]; bitmap.CopyPixels(data, 1280 * 4, 0);
            Assert.True(Enumerable.Range(0, 1280 * 240).Count(i => data[i * 4 + 2] > 150 && data[i * 4] < 70) > 50);
            if (withRoles)
                Assert.True(Enumerable.Range(1280 * 360, 1280 * 360).Count(i => data[i * 4] > 150 && data[i * 4 + 2] < 70) > 50);
            Assert.Empty(Directory.GetFiles(root, "*.partial.mp4"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(AnnotationType.Pinyin)]
    [InlineData(AnnotationType.Translation)]
    public async Task WholeLineAnnotationSurvivesRolesWithoutFlatteningWordAssignments(AnnotationType type)
    {
        var p = Project(); var line = p.Lyrics.Lines[0];
        line = line with { AnnotationType = type, AnnotationText = "helper text", TranslationText = "helper text",
            Units = line.Units.Select((u, i) => u with { RoleIds = [p.Roles[i].Id] }).ToArray() };
        line = line with { Presentation = LanguagePresentation.BuildPlain(line) };
        await using var renderer = new WpfSubtitleFrameRenderer(p with { Lyrics = p.Lyrics with { Lines = [line] } });
        var frame = await renderer.RenderAsync(ProjectTime.FromSeconds(3.5), 1280, 720);
        Assert.True(Pixels(frame, true).Max(v => v.Y) - Pixels(frame, true).Min(v => v.Y) > 65);
        Assert.True(Pixels(frame, false).Min(v => v.X) > Pixels(frame, true).Min(v => v.X));
        Snapshot(frame, $"role-annotation-{type}.png");
    }

    private static KaraokeProject Project()
    {
        var p = KaraokeProject.Create("Roles", DateTimeOffset.UnixEpoch);
        var doc = new LyricImportService().Parse("Hello world", ".txt", LyricLanguage.English);
        var line = doc.Lines[0] with { Timing = new(ProjectTime.Zero, ProjectTime.FromSeconds(5)),
            Units = doc.Lines[0].Units.Select((u, i) => u with { Timing = new(ProjectTime.FromSeconds(i), ProjectTime.FromSeconds(i + 1)), IsConfirmed = true }).ToArray() };
        SubtitleStyle Style(string color) => p.SubtitleStyle with { InactiveColor = color, ActiveColor = color, OutlineWidth = 0, ShadowOffset = 0, PrimaryFontSize = 100 };
        return p with { Lyrics = doc with { Lines = [line] }, Roles = [new(Guid.NewGuid(), "A", Style("#FFFF0000")), new(Guid.NewGuid(), "B", Style("#FF0000FF"))] };
    }
    private static byte[] Alpha(RenderedFrame frame) => frame.BgraPixels.ToArray().Where((_, i) => i % 4 == 3).ToArray();
    private static List<(int X, int Y)> Pixels(RenderedFrame frame, bool red)
    {
        var result = new List<(int, int)>(); var bytes = frame.BgraPixels.Span;
        for (int i = 0; i < bytes.Length; i += 4)
            if (bytes[i + (red ? 2 : 0)] > 150 && bytes[i + (red ? 0 : 2)] < 60 && bytes[i + 3] > 150)
                result.Add((i / 4 % frame.Width, i / 4 / frame.Width));
        return result;
    }
    private static void Snapshot(RenderedFrame frame, string file)
    {
        var root = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR"); if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.BgraPixels.ToArray(), frame.Stride);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(root, file)); encoder.Save(stream);
    }
}
