using KaraokeFilmMaker.Infrastructure.Japanese;
using KaraokeFilmMaker.Application.Japanese;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class MeCabJapaneseReadingTests
{
    [Fact]
    public async Task BundledDictionaryAndAnnotationProduceTheRequestedSingingGroups()
    {
        using var reading = new MeCabJapaneseReadingService();
        var document = new LyricImportService().Parse("一緒に行きましょうといったら、すぐにディスコタイムをスタット。", ".txt", LyricLanguage.Japanese);
        document = await new JapaneseAnnotationService(reading).AnnotateAsync(document);
        Assert.Equal("いっ|しょ|に|い|き|ま|しょ|う|と|いっ|た|ら|す|ぐ|に|でぃ|す|こ|た|い|む|を|す|たっ|と".Split('|'),
            document.Lines[0].Units.Select(unit => unit.Reading));
        Assert.Null(JapaneseAnnotationService.GetRecordingIssue(document));
    }

    [Fact]
    public async Task BundledDictionaryProducesEditableReadingForJapaneseKanji()
    {
        const string text = "未来へ進む";
        using var service = new MeCabJapaneseReadingService();
        var tokens = await service.AnalyzeAsync(text);
        Assert.NotEmpty(tokens);
        Assert.All(tokens, token =>
        {
            Assert.False(string.IsNullOrWhiteSpace(token.Reading));
            Assert.InRange(token.TextStart, 0, text.Length - 1);
            Assert.InRange(token.TextLength, 1, text.Length - token.TextStart);
        });
        Assert.Contains(tokens, token => token.Reading.Contains("みらい", StringComparison.Ordinal));
        Assert.Contains(tokens, token => token.Reading.Contains("すす", StringComparison.Ordinal));
    }
}
