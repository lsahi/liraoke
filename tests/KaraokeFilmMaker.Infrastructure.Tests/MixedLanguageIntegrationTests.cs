using System.Text.Json;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Validation;
using KaraokeFilmMaker.Infrastructure.Japanese;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Infrastructure.Rendering;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class MixedLanguageIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Liraoke.MixedLanguageIntegration", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RealDictionaryMixedImportKeepsSingingUnitsSeparateFromSubtitleRubyAfterSave()
    {
        KaraokeProject project = await CreateAnalyzedProjectAsync();
        LyricLine mixed = project.Lyrics.Lines[0];
        Assert.Equal(new[] { LanguageIds.Japanese, LanguageIds.English }, mixed.LanguageSpans!.Select(span => span.LanguageId));
        Assert.All(mixed.LanguageSpans!, span => Assert.False(span.NeedsReview));
        Assert.Contains(mixed.Units, unit => unit.LanguageId == LanguageIds.Japanese && unit.Reading == "しっ");
        Assert.DoesNotContain(mixed.Units, unit => unit.Reading == "っ");
        Assert.Equal(new[] { "I", "love", "you" }, mixed.Units.Where(unit => unit.LanguageId == LanguageIds.English).Select(unit => unit.Reading));
        Assert.Equal("はし", Assert.Single(LyricPresentationLayout.BuildSubtitleGroups(mixed, false), group => group.Text == "走").Annotation);
        Assert.Equal("", Assert.Single(LyricPresentationLayout.BuildSubtitleGroups(mixed, false), group => group.Text == "っ").Annotation);
        Assert.Equal(LanguageIds.Chinese, Assert.Single(project.Lyrics.Lines[1].LanguageSpans!).LanguageId);
        Assert.Equal(new[] { "我", "们", "一", "起", "唱" }, project.Lyrics.Lines[1].Units.Select(unit => unit.Reading));
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(project.Lyrics));
        Assert.Empty(ProjectValidator.Validate(project));

        var repository = new JsonProjectRepository();
        await repository.SaveAsync(_root, project);
        KaraokeProject reopened = await repository.LoadAsync(_root);
        Assert.Equal(JsonSerializer.Serialize(project), JsonSerializer.Serialize(reopened));
        Assert.All(reopened.Lyrics.Lines.SelectMany(line => line.Units), unit =>
        {
            Assert.True(unit.IsConfirmed);
            Assert.Equal(unit.Timing.Start, unit.RawInputTime);
        });
        Assert.Equal("はし", Assert.Single(LyricPresentationLayout.BuildSubtitleGroups(reopened.Lyrics.Lines[0], false), group => group.Text == "走").Annotation);
    }

    [Fact]
    public async Task SavedMixedPresentationRendersIdenticalFramesWithoutAnalyzers()
    {
        // The real dictionary is disposed before persistence and rendering. Neither operation
        // receives a language registry or can re-run language policies.
        KaraokeProject project = await CreateAnalyzedProjectAsync();
        var repository = new JsonProjectRepository();
        await repository.SaveAsync(_root, project);
        KaraokeProject reopened = await repository.LoadAsync(_root);
        string beforeRendering = JsonSerializer.Serialize(reopened);
        byte[]? kanaPixels = null;
        foreach (bool romanized in new[] { false, true })
        {
            var original = project with { SubtitleStyle = project.SubtitleStyle with { ShowRomanization = romanized } };
            var saved = reopened with { SubtitleStyle = reopened.SubtitleStyle with { ShowRomanization = romanized } };
            await using var originalRenderer = new WpfSubtitleFrameRenderer(original);
            await using var savedRenderer = new WpfSubtitleFrameRenderer(saved);
            var expected = await originalRenderer.RenderAsync(ProjectTime.FromSeconds(2), 1920, 1080);
            var actual = await savedRenderer.RenderAsync(ProjectTime.FromSeconds(2), 1920, 1080);
            byte[] pixels = actual.BgraPixels.ToArray();
            Assert.Equal(expected.BgraPixels.ToArray(), pixels);
            Assert.Contains(pixels, value => value > 0);
            if (!romanized) { kanaPixels = pixels; }
            else { Assert.False(kanaPixels!.AsSpan().SequenceEqual(pixels)); }
        }
        Assert.Equal(beforeRendering, JsonSerializer.Serialize(reopened));
    }

    private static async Task<KaraokeProject> CreateAnalyzedProjectAsync()
    {
        using var dictionary = new MeCabJapaneseReadingService();
        var analysis = new LanguageAnalysisService(LanguageRegistry.CreateBuiltIns(dictionary));
        LyricDocument imported = new LyricImportService().Parse(
            "[00:01]駅まで走ってった I love you\n[00:08]我们一起唱", ".lrc", LyricLanguage.Auto);
        LyricDocument analyzed = await analysis.AnalyzeDocumentAsync(imported);
        analyzed = analyzed with
        {
            Lines = analyzed.Lines.Select(line => line with
            {
                Units = line.Units.Select(unit => unit with
                {
                    IsConfirmed = true,
                    RawInputTime = unit.Timing.Start,
                }).ToArray(),
            }).ToArray(),
        };
        return KaraokeProject.Create("Mixed language integration", DateTimeOffset.UtcNow) with { Lyrics = analyzed };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
