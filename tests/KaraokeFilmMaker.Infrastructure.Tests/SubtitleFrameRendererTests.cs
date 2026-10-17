using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Rendering;
using KaraokeFilmMaker.Application.Japanese;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Infrastructure.Japanese;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Application.Languages;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class SubtitleFrameRendererTests
{
    [Theory]
    [InlineData("English", false)]
    [InlineData("EnglishSnapshot", false)]
    [InlineData("ChineseSnapshot", false)]
    [InlineData("Japanese", false)]
    [InlineData("JapaneseSnapshot", false)]
    [InlineData("JapaneseSnapshot", true)]
    [InlineData("ThirdPartySnapshot", false)]
    public async Task PunctuationPixelsCompleteAtWordBoundariesInEverySharedRenderPath(string path, bool romanized)
    {
        bool japanese = path.StartsWith("Japanese", StringComparison.Ordinal);
        bool chinese = path.StartsWith("Chinese", StringComparison.Ordinal);
        string text = japanese ? "「駅、歌！」" : chinese ? "「你，好！」" : "\"La, la!\"";
        var units = new[]
        {
            new LyricUnit(Guid.NewGuid(), [new TextRange(1, japanese || chinese ? 1 : 2)],
                japanese ? "えき" : chinese ? "你" : "La", japanese ? "eki" : null,
                new TimeRange(ProjectTime.FromSeconds(2), ProjectTime.FromSeconds(3)), null, true),
            new LyricUnit(Guid.NewGuid(), [new TextRange(japanese || chinese ? 3 : 5, japanese || chinese ? 1 : 2)],
                japanese ? "うた" : chinese ? "好" : "la", japanese ? "uta" : null,
                new TimeRange(ProjectTime.FromSeconds(5), ProjectTime.FromSeconds(7)), null, true),
        };
        var line = new LyricLine(Guid.NewGuid(), text, AnnotationType.None, null, null,
            new TimeRange(ProjectTime.Zero, ProjectTime.FromSeconds(10)), units, ReviewState.Confirmed, null);
        if (path.EndsWith("Snapshot", StringComparison.Ordinal))
        {
            var presentation = japanese ? LanguagePresentation.BuildJapanese(line) : LanguagePresentation.BuildPlain(line);
            if (path == "ThirdPartySnapshot")
            {
                // Only persisted host-readable runs are present; no plugin is installed or called.
                presentation = presentation with { SubtitleRuns = presentation.SubtitleRuns
                    .Select(run => run with { LanguageId = "test-third-party-language" }).ToArray() };
            }
            line = line with { Presentation = presentation };
        }
        string original = System.Text.Json.JsonSerializer.Serialize(line);
        var project = KaraokeProject.Create("Punctuation " + path, DateTimeOffset.UtcNow) with
        {
            Lyrics = new LyricDocument(japanese ? LyricLanguage.Japanese : chinese ? LyricLanguage.Chinese : LyricLanguage.English, [line]),
        };
        // This regression counts white fill as unsung. A white sung outline has its own pixel tests.
        project = project with { SubtitleStyle = project.SubtitleStyle with { ShowRomanization = romanized, ActiveOutlineColor = null } };
        await using var renderer = new WpfSubtitleFrameRenderer(project);
        var beforeFirst = await renderer.RenderAsync(ProjectTime.FromSeconds(1.999999), 1920, 1080);
        var firstStart = await renderer.RenderAsync(ProjectTime.FromSeconds(2), 1920, 1080);
        Assert.Equal(0, CountGold(beforeFirst));
        Assert.True(CountGold(firstStart) > 3, "The opening quote must already be gold at the first word's exact start.");

        var beforeSecond = await renderer.RenderAsync(ProjectTime.FromSeconds(4.999999), 1920, 1080);
        var secondStart = await renderer.RenderAsync(ProjectTime.FromSeconds(5), 1920, 1080);
        Assert.True(CountGold(secondStart) > CountGold(beforeSecond) + 3,
            "Only the comma changes at this boundary: it must be gold when the next word starts.");
        var beforeComplete = await renderer.RenderAsync(ProjectTime.FromSeconds(6.999999), 1920, 1080);
        var complete = await renderer.RenderAsync(ProjectTime.FromSeconds(7), 1920, 1080);
        Assert.True(CountGold(complete) > CountGold(beforeComplete) + 3,
            "Trailing punctuation completes with the last word, not the later line end.");
        Assert.Equal(0, CountVisibleWhite(complete, 0, complete.Height));
        var seekBack = await renderer.RenderAsync(ProjectTime.FromSeconds(5), 1920, 1080);
        Assert.Equal(secondStart.BgraPixels.ToArray(), seekBack.BgraPixels.ToArray());
        Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(line));
        SaveSnapshot($"punctuation-{path}-{(romanized ? "romaji" : "plain")}-next-word.png", secondStart);
        SaveSnapshot($"punctuation-{path}-{(romanized ? "romaji" : "plain")}-complete.png", complete);
    }

    [Fact]
    public async Task StationLyricRendersHashiOverHashiruKanjiWithoutRetimingSavedUnits()
    {
        using var dictionary = new MeCabJapaneseReadingService();
        var document = await new JapaneseAnnotationService(dictionary).AnnotateAsync(
            new LyricImportService().Parse("[00:01]駅まで走ってった", ".lrc", LyricLanguage.Japanese));
        var line = document.Lines[0];
        Assert.Contains(line.Units, unit => unit.Reading == "しっ");
        Assert.Contains(JapaneseRubyBuilder.BuildTimingGroups(line, false), group => group.Text == "走っ");
        line = line with { Units = line.Units.Select(unit => unit with { IsConfirmed = true, RawInputTime = unit.Timing.Start }).ToArray() };
        var project = KaraokeProject.Create("駅まで走ってった", DateTimeOffset.UtcNow) with { Lyrics = document with { Lines = [line] } };
        string root = Path.Combine(Path.GetTempPath(), "Liraoke.RubyTests", Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new JsonProjectRepository();
            await repository.SaveAsync(root, project); project = await repository.LoadAsync(root);
            var saved = System.Text.Json.JsonSerializer.Serialize(project);
            var groups = JapaneseRubyBuilder.Build(project.Lyrics.Lines[0], false);
            Assert.Equal("はし", Assert.Single(groups, group => group.Text == "走").Annotation);
            Assert.Equal("", Assert.Single(groups, group => group.Text == "っ").Annotation);
            Assert.Equal("えき", Assert.Single(groups, group => group.Text == "駅").Annotation);
            await using var renderer = new WpfSubtitleFrameRenderer(project);
            var actual = await renderer.RenderAsync(ProjectTime.FromSeconds(1), 1920, 1080);

            // An independent, explicitly mapped display reference must produce identical pixels.
            string[] readings = ["えき", "ま", "で", "はし", "っ", "てっ", "た"];
            TextRange[] ranges = [new(0, 1), new(1, 1), new(2, 1), new(3, 1), new(4, 1), new(5, 2), new(7, 1)];
            var reference = line with { Units = readings.Select((reading, i) => new LyricUnit(Guid.NewGuid(), [ranges[i]], reading,
                null, new TimeRange(ProjectTime.FromSeconds(1 + i * 0.1), ProjectTime.FromSeconds(1 + (i + 1) * 0.1)), null, true)).ToArray() };
            await using var referenceRenderer = new WpfSubtitleFrameRenderer(project with { Lyrics = document with { Lines = [reference] } });
            var expected = await referenceRenderer.RenderAsync(ProjectTime.FromSeconds(1), 1920, 1080);
            Assert.Equal(expected.BgraPixels.ToArray(), actual.BgraPixels.ToArray());
            Assert.Equal(saved, System.Text.Json.JsonSerializer.Serialize(project));
            SaveSnapshot("station-orthographic-ruby.png", actual);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [Fact]
    public async Task JapaneseKanaRubyAndOptInRomajiRenderDifferentMappedAnnotations()
    {
        using var dictionary = new MeCabJapaneseReadingService();
        var document = new LyricImportService().Parse("[00:01]一緒に行きましょうといったら、すぐにディスコタイムをスタット。\n[00:08]戦場ヶ原へ", ".lrc", LyricLanguage.Japanese);
        document = await new JapaneseAnnotationService(dictionary).AnnotateAsync(document);
        document = document with { Lines = [document.Lines[0], JapaneseAnnotationService.ApplyManualReading(document.Lines[1], "せんじょうがはらへ")] };
        var project = KaraokeProject.Create("Ruby", DateTimeOffset.UtcNow) with { Lyrics = document };
        Assert.False(project.SubtitleStyle.ShowRomanization);
        await using var kanaRenderer = new WpfSubtitleFrameRenderer(project);
        var kana = await kanaRenderer.RenderAsync(ProjectTime.FromSeconds(2), 1920, 1080);
        await using var romajiRenderer = new WpfSubtitleFrameRenderer(project with { SubtitleStyle = project.SubtitleStyle with { ShowRomanization = true } });
        var romaji = await romajiRenderer.RenderAsync(ProjectTime.FromSeconds(2), 1920, 1080);
        Assert.False(kana.BgraPixels.Span.SequenceEqual(romaji.BgraPixels.Span));
        Assert.True(CountGold(kana) > 0); Assert.True(CountGold(romaji) > 0);
        SaveSnapshot("japanese-kana-ruby.png", kana);
        SaveSnapshot("japanese-romaji-ruby.png", romaji);
    }

    [Fact]
    public async Task EmptyProjectProducesTransparentFrame()
    {
        await using var renderer = new WpfSubtitleFrameRenderer(KaraokeProject.Create("Empty", DateTimeOffset.UtcNow));
        RenderedFrame frame = await renderer.RenderAsync(ProjectTime.Zero, 640, 360);
        Assert.Equal(640 * 4, frame.Stride);
        Assert.Equal(640 * 360 * 4, frame.BgraPixels.Length);
        Assert.All(frame.BgraPixels.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task CjkFramesRemainInBottomRegionAndSweepMorePixelsAsTimeAdvances()
    {
        KaraokeProject project = CreateProject();
        await using var renderer = new WpfSubtitleFrameRenderer(project);
        RenderedFrame start = await renderer.RenderAsync(ProjectTime.Zero, 1920, 1080);
        RenderedFrame middle = await renderer.RenderAsync(ProjectTime.FromSeconds(2.5), 1920, 1080);
        RenderedFrame complete = await renderer.RenderAsync(ProjectTime.FromSeconds(7.5), 1920, 1080);

        Assert.Equal(0, CountGold(start));
        Assert.True(CountVisibleWhite(start, firstRow: 756, lastRow: 820) > 100);
        Assert.True(CountGold(middle) > 0);
        Assert.True(CountGold(complete) > CountGold(middle));
        byte[] pixels = complete.BgraPixels.ToArray();
        for (var index = 3; index < 1920 * 756 * 4; index += 4)
        {
            Assert.Equal(0, pixels[index]);
        }

        SaveSnapshot("subtitle-start.png", start);
        SaveSnapshot("subtitle-middle.png", middle);
        SaveSnapshot("subtitle-complete.png", complete);
    }

    [Fact]
    public async Task RenderingSameTimestampAfterSeekingIsDeterministic()
    {
        await using var renderer = new WpfSubtitleFrameRenderer(CreateProject());
        RenderedFrame original = await renderer.RenderAsync(ProjectTime.FromSeconds(2), 960, 540);
        _ = await renderer.RenderAsync(ProjectTime.FromSeconds(6), 960, 540);
        RenderedFrame soughtBack = await renderer.RenderAsync(ProjectTime.FromSeconds(2), 960, 540);
        Assert.Equal(original.BgraPixels.ToArray(), soughtBack.BgraPixels.ToArray());
    }

    private static int CountGold(RenderedFrame frame)
    {
        int count = 0;
        ReadOnlySpan<byte> pixels = frame.BgraPixels.Span;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index + 3] > 200 && pixels[index + 2] > 230
                && pixels[index + 1] is > 180 and < 230 && pixels[index] < 110)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountVisibleWhite(RenderedFrame frame, int firstRow, int lastRow)
    {
        int count = 0;
        ReadOnlySpan<byte> pixels = frame.BgraPixels.Span;
        for (int index = firstRow * frame.Stride; index < lastRow * frame.Stride; index += 4)
        {
            if (pixels[index + 3] > 200 && pixels[index] > 230
                && pixels[index + 1] > 230 && pixels[index + 2] > 230)
            {
                count++;
            }
        }

        return count;
    }

    private static KaraokeProject CreateProject()
    {
        List<LyricUnit> units = [];
        string[] reading = ["み", "ら", "い", "へ", "す", "す", "む"];
        for (var index = 0; index < reading.Length; index++)
        {
            var range = index < 3 ? new TextRange(0, 2)
                : index == 3 ? new TextRange(2, 1) : new TextRange(3, 2);
            units.Add(new LyricUnit(Guid.NewGuid(), [range], reading[index], null,
                new TimeRange(ProjectTime.FromSeconds(index), ProjectTime.FromSeconds(index + 1)), null, true));
        }

        var line = new LyricLine(Guid.NewGuid(), "未来へ進む", AnnotationType.Kana,
            "みらいへすすむ", null, new TimeRange(ProjectTime.Zero, ProjectTime.FromSeconds(8)),
            units, ReviewState.Confirmed, ReadingSource.Confirmed, ReadingText: "みらいへすすむ");
        var next = new LyricLine(Guid.NewGuid(), "下一句歌词，继续前行", AnnotationType.Translation,
            null, "次の歌詞へ", new TimeRange(ProjectTime.FromSeconds(10), ProjectTime.FromSeconds(15)),
            [], ReviewState.Unreviewed, null);
        return KaraokeProject.Create("Snapshot", DateTimeOffset.UtcNow) with
        {
            Lyrics = new LyricDocument(LyricLanguage.Japanese, [line, next]),
        };
    }

    private static void SaveSnapshot(string name, RenderedFrame frame)
    {
        string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        BitmapSource bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96,
            PixelFormats.Bgra32, null, frame.BgraPixels.ToArray(), frame.Stride);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }
}
