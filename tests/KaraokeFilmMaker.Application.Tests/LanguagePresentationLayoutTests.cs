using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class LanguagePresentationLayoutTests
{
    [Fact]
    public void SubtitleRubyAndRomanizationCanHaveDifferentSourceBoundaries()
    {
        var first = Unit("は", new(0, 2), 0, 1);
        var second = Unit("しっ", new(0, 2), 1, 2);
        var line = Line("走っ", [first, second]) with
        {
            Presentation = new("走っ", [],
                [new(new(0, 1), "はし", null, [first.Id, second.Id]), new(new(1, 1), "", null, [second.Id])],
                RomanizedSubtitleRuns: [new(new(0, 2), "", "ha shit", [first.Id, second.Id])]),
        };

        var kana = LyricPresentationLayout.BuildSubtitleGroups(line, false);
        Assert.Equal("はし", Assert.Single(kana, group => group.Text == "走").Annotation);
        Assert.Empty(Assert.Single(kana, group => group.Text == "っ").Annotation);
        var romaji = Assert.Single(LyricPresentationLayout.BuildSubtitleGroups(line, true));
        Assert.Equal("走っ", romaji.Text);
        Assert.Equal("ha shit", romaji.Annotation);
        Assert.Equal(new[] { 0, 1 }, romaji.UnitIndexes);
    }

    [Fact]
    public void MixedRunsPreserveGapsAndUseTheirOwnAnnotations()
    {
        var line = Line("君 love 你", []) with
        {
            Presentation = new("君 love 你", [],
                [new(new(0, 1), "きみ", "kimi", [], LanguageIds.Japanese),
                 new(new(2, 4), "", null, [], LanguageIds.English),
                 new(new(7, 1), "ni", null, [], LanguageIds.Chinese)]),
        };

        var groups = LyricPresentationLayout.BuildSubtitleGroups(line, false);
        Assert.Equal(line.PrimaryText, string.Concat(groups.Select(group => group.Text)));
        Assert.Equal("きみ", groups[0].Annotation);
        Assert.Equal("", Assert.Single(groups, group => group.Text == "love").Annotation);
        Assert.Equal("ni", groups[^1].Annotation);
        Assert.Equal("kimi", LyricPresentationLayout.BuildSubtitleGroups(line, true)[0].Annotation);
    }

    [Fact]
    public void StaleOrFutureSnapshotsDoNotReplaceLegacyRendering()
    {
        var line = Line("君", []) with { Presentation = new("他", [], []) };
        Assert.False(LyricPresentationLayout.HasCurrentSnapshot(line));
        Assert.Empty(LyricPresentationLayout.BuildSubtitleGroups(line, false));
        Assert.False(LyricPresentationLayout.HasCurrentSnapshot(line with { Presentation = new("君", [], [], FormatVersion: 99) }));
    }

    [Fact]
    public void ReplacedUnitIdsInvalidateOtherwiseUnchangedSourceSnapshot()
    {
        var unit = Unit("きみ", new(0, 1), 0, 2);
        var line = Line("君", [unit]) with { Presentation = new("君", [new(unit.Id, "きみ", "君")],
            [new(new(0, 1), "きみ", null, [unit.Id])]) };
        Assert.True(LyricPresentationLayout.HasCurrentSnapshot(line));
        var edited = line with { Units = [unit with { Id = Guid.NewGuid() }] };
        Assert.False(LyricPresentationLayout.HasCurrentSnapshot(edited));
        Assert.Empty(LyricPresentationLayout.BuildSubtitleGroups(edited, false));
    }

    [Fact]
    public void MissingAndOverlappingPluginRunsNeverDropOriginalWhitespaceOrPunctuation()
    {
        var line = Line("「君、 love! 你。」", []) with
        {
            Presentation = new("「君、 love! 你。」", [],
                [new(new(1, 1), "きみ", null, []), new(new(4, 4), "", null, []),
                 new(new(5, 2), "overlap", null, []), new(new(40, 1), "invalid", null, [])]),
        };
        foreach (bool romanized in new[] { false, true })
        {
            var groups = LyricPresentationLayout.BuildSubtitleGroups(line, romanized);
            Assert.Equal(line.PrimaryText, string.Concat(groups.Select(group => group.Text)));
            Assert.DoesNotContain(groups, group => group.Annotation is "overlap" or "invalid");
        }
    }

    [Fact]
    public void SceneCarriesUnitProgressForPersistedAnnotationMappings()
    {
        var unit = Unit("きみ", new(0, 1), 0, 2);
        var line = Line("君", [unit]) with { Presentation = new("君", [], [new(new(0, 1), "きみ", null, [unit.Id])]) };
        var builder = new SubtitleSceneBuilder();
        var scene = Assert.Single(builder.Build(new(LyricLanguage.English, [line]), ProjectTime.FromSeconds(1)).Lines);
        Assert.NotNull(scene.UnitProgress);
        Assert.Equal(0.5, scene.UnitProgress[unit.Id]);
        var disabled = Assert.Single(builder.Build(new(LyricLanguage.Japanese, [line with { IsHighlightEnabled = false }]), ProjectTime.FromSeconds(1)).Lines);
        Assert.Null(disabled.UnitProgress);
    }

    private static LyricLine Line(string text, IReadOnlyList<LyricUnit> units) => new(Guid.NewGuid(), text,
        AnnotationType.None, null, null, new(ProjectTime.Zero, ProjectTime.FromSeconds(3)), units, ReviewState.Unreviewed, null);
    private static LyricUnit Unit(string reading, TextRange range, double start, double end) => new(Guid.NewGuid(),
        [range], reading, null, new(ProjectTime.FromSeconds(start), ProjectTime.FromSeconds(end)), null, false);
}
