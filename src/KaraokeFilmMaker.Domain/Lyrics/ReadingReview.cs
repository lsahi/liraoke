namespace KaraokeFilmMaker.Domain.Lyrics;

public sealed record ReadingReviewIssue(TextRange Range, string Reason);
public sealed record ReadingSuggestion(string Surface, string Reading, string Language);
public sealed record ReadingReview(string Status, string Message, string OriginalText, string? SuggestedText,
    IReadOnlyList<ReadingSuggestion> Suggestions, IReadOnlyList<ReadingReviewIssue> Issues);
