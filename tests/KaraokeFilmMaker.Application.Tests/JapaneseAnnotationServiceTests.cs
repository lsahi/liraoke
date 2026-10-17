using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Japanese;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class JapaneseAnnotationServiceTests
{
    [Fact]
    public void RomanizesCommonKanaRules()
    {
        Assert.Equal("kyou", KanaRomanizer.Romanize("きょう"));
        Assert.Equal("gakkou", KanaRomanizer.Romanize("がっこう"));
        Assert.Equal("suupaa", KanaRomanizer.Romanize("スーパー"));
    }

    [Fact]
    public async Task MapsMultipleMoraToOneKanjiWord()
    {
        var service = new JapaneseAnnotationService(new StubReadingService());
        LyricLine source = CreateLine("未来へ進む", ReadingSource: null);

        LyricLine result = await service.AnnotateLineAsync(source);

        Assert.Equal("みらいへすすむ", result.ReadingText);
        Assert.Equal("mirai e susumu", result.RomanizationText);
        Assert.Equal(7, result.Units.Count);
        Assert.Equal("e", result.Units[3].Romanization);
        Assert.All(
            result.Units.Take(3),
            unit => Assert.Equal(new TextRange(0, 2), Assert.Single(unit.DisplayRanges)));
    }

    [Theory]
    [InlineData("！")]
    [InlineData("、")]
    public async Task PunctuationKeepsItsDisplayTextWithoutCreatingSingingUnits(string punctuation)
    {
        var service = new JapaneseAnnotationService(new SingleTokenReadingService(punctuation));
        LyricLine result = await service.AnnotateLineAsync(CreateLine(punctuation, null));
        Assert.Empty(result.Units);
        Assert.Equal(punctuation, result.PrimaryText);
    }

    [Fact]
    public async Task UnknownKanjiReadingRemainsEditableAndIsMarkedForReview()
    {
        const string unknown = "𠮷";
        var service = new JapaneseAnnotationService(new SingleTokenReadingService(unknown));
        LyricLine result = await service.AnnotateLineAsync(CreateLine(unknown, null));
        Assert.Equal(unknown, result.ReadingText);
        Assert.Equal(ReviewState.ReviewLater, result.ReviewState);
        Assert.NotEmpty(result.Units);
        Assert.Equal(ReadingSource.Generated, result.ReadingSource);
    }

    [Theory]
    [InlineData(ReadingSource.Confirmed)]
    [InlineData(ReadingSource.Manual)]
    public async Task DoesNotOverwriteUserApprovedReading(ReadingSource source)
    {
        var service = new JapaneseAnnotationService(new ThrowingReadingService());
        LyricLine line = CreateLine("未来", source) with { ReadingText = "みく" };

        LyricLine result = await service.AnnotateLineAsync(line);

        Assert.Same(line, result);
        Assert.Equal("みく", result.ReadingText);
    }

    private static LyricLine CreateLine(string text, ReadingSource? ReadingSource) =>
        new(
            Guid.NewGuid(),
            text,
            AnnotationType.None,
            AnnotationText: null,
            TranslationText: null,
            new TimeRange(ProjectTime.Zero, ProjectTime.FromSeconds(7)),
            Units: [],
            ReviewState.Unreviewed,
            ReadingSource);

    private sealed class StubReadingService : IJapaneseReadingService
    {
        public Task<IReadOnlyList<JapaneseToken>> AnalyzeAsync(
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JapaneseToken>>(
            [
                new("未来", "みらい", "mirai", 0, 2, true),
                new("へ", "へ", "e", 2, 1, false),
                new("進む", "すすむ", "susumu", 3, 2, true),
            ]);
    }

    private sealed class ThrowingReadingService : IJapaneseReadingService
    {
        public Task<IReadOnlyList<JapaneseToken>> AnalyzeAsync(
            string text,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Confirmed text must not be analyzed again.");
    }

    private sealed class SingleTokenReadingService(string value) : IJapaneseReadingService
    {
        public Task<IReadOnlyList<JapaneseToken>> AnalyzeAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JapaneseToken>>([new(value, value, value, 0, value.Length, false)]);
    }
}
