namespace KaraokeFilmMaker.Application.Timing;

/// <param name="Confidence">Heuristic rhythmic stability in [0, 1], not a probability of correctness.</param>
/// <param name="OffsetMilliseconds">First nonnegative grid point relative to the original media, not a bar/downbeat.</param>
public sealed record TempoAnalysisResult(
    double? BeatsPerMinute,
    int? OffsetMilliseconds,
    double Confidence,
    bool IsReliable,
    string Message,
    IReadOnlyList<double> AlternativeBeatsPerMinute);
