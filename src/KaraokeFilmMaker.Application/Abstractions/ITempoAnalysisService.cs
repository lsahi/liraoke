using KaraokeFilmMaker.Application.Timing;

namespace KaraokeFilmMaker.Application.Abstractions;

/// <summary>Estimates a constant beat grid from the original, unscaled song, without changing the project.</summary>
public interface ITempoAnalysisService
{
    Task<TempoAnalysisResult> AnalyzeAsync(string sourcePath, CancellationToken cancellationToken = default);
}
