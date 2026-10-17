namespace KaraokeFilmMaker.Application.Abstractions;

public interface IJapaneseReadingService
{
    Task<IReadOnlyList<JapaneseToken>> AnalyzeAsync(
        string text,
        CancellationToken cancellationToken = default);
}

public sealed record JapaneseToken(
    string Surface,
    string Reading,
    string Romanization,
    int TextStart,
    int TextLength,
    bool RequiresReview);
