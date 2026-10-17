using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Rendering;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class SubtitleEntranceRenderingTests
{
    [Theory]
    [InlineData(LyricLanguage.Chinese, false, false)]
    [InlineData(LyricLanguage.English, false, true)]
    [InlineData(LyricLanguage.Japanese, false, false)]
    [InlineData(LyricLanguage.Japanese, true, false)]
    [InlineData(LyricLanguage.Japanese, true, true)]
    public async Task CueFillsThenDisappearsWithoutMovingPrimaryOrRuby(LyricLanguage language, bool snapshot, bool bottom)
    {
        bool japanese = language == LyricLanguage.Japanese;
        string text = japanese ? "歌" : language == LyricLanguage.English ? "Sing with me" : "唱一首歌";
        var line = new LyricLine(Guid.NewGuid(), text, AnnotationType.None, null, null,
            new(ProjectTime.FromSeconds(20), ProjectTime.FromSeconds(22)),
            [new(Guid.NewGuid(), [new(0, text.Length)], japanese ? "うた" : text, japanese ? "uta" : null,
                new(ProjectTime.FromSeconds(20), ProjectTime.FromSeconds(22)), null, true)], ReviewState.Confirmed, null);
        if (snapshot) { line = line with { Presentation = LanguagePresentation.BuildJapanese(line) }; }
        var previous = line with { Id = Guid.NewGuid(), Timing = new(ProjectTime.Zero, ProjectTime.FromSeconds(1)), Units = [], Presentation = null };
        var project = KaraokeProject.Create("进句提示", DateTimeOffset.UtcNow) with
        { Lyrics = new(language, bottom ? [previous, line] : [line]) };
        await using var renderer = new WpfSubtitleFrameRenderer(project);
        var hidden = await renderer.RenderAsync(ProjectTime.FromSeconds(13.9), 1280, 720);
        Assert.All(hidden.BgraPixels.ToArray(), value => Assert.Equal(0, value));
        var waiting = await renderer.RenderAsync(ProjectTime.FromSeconds(14), 1280, 720);
        var counting = await renderer.RenderAsync(ProjectTime.FromSeconds(18.5), 1280, 720);
        var singing = await renderer.RenderAsync(ProjectTime.FromSeconds(20), 1280, 720);
        Assert.True(Yellow(counting) > Yellow(waiting));
        Assert.Equal(0, Yellow(singing));
        byte[] before = waiting.BgraPixels.ToArray(), after = singing.BgraPixels.ToArray();
        int left = 1280, right = -1, visiblePrimary = 0;
        for (int y = 0; y < 720; y++)
        for (int x = 0; x < 1280; x++)
        {
            int i = y * waiting.Stride + x * 4;
            if (!before.AsSpan(i, 4).SequenceEqual(after.AsSpan(i, 4)))
            { left = Math.Min(left, x); right = Math.Max(right, x); Assert.Equal(0, after[i + 3]); }
            if (after[i + 3] > 0) { visiblePrimary++; }
        }
        Assert.True(visiblePrimary > 100); Assert.InRange(right - left, 1, 48);
        Assert.Equal(counting.BgraPixels.ToArray(), (await renderer.RenderAsync(ProjectTime.FromSeconds(18.5), 1280, 720)).BgraPixels.ToArray());
        Save(waiting, $"entrance-start-{language}-{snapshot}-{bottom}.png");
        Save(counting, $"entrance-counting-{language}-{snapshot}-{bottom}.png");
        Save(singing, $"entrance-singing-{language}-{snapshot}-{bottom}.png");
    }

    private static int Yellow(RenderedFrame frame)
    {
        var pixels = frame.BgraPixels.Span; int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        { if (pixels[i + 3] > 200 && pixels[i + 2] > 240 && pixels[i + 1] > 180 && pixels[i] < 120) { count++; } }
        return count;
    }
    private static void Save(RenderedFrame frame, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
        if (directory is null) { return; }
        Directory.CreateDirectory(directory);
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.BgraPixels.ToArray(), frame.Stride);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
    }
}
