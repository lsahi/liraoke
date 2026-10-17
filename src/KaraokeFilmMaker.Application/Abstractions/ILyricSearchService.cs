namespace KaraokeFilmMaker.Application.Abstractions;

public sealed record LyricSearchQuery(string Title, string? Artist = null);

public sealed record OnlineLyricResult(long Id, string Title, string Artist, string Album, double DurationSeconds,
    bool Instrumental, string? PlainLyrics, string? SyncedLyrics, string Provider, string SourceUrl)
{
    public bool HasTiming => !string.IsNullOrWhiteSpace(SyncedLyrics);
    public bool CanImport => !Instrumental && (HasTiming || !string.IsNullOrWhiteSpace(PlainLyrics));
    public string FormatDescription => Instrumental ? "纯音乐" : HasTiming ? "LRC / 逐行时间" : "纯文本";
    public string DurationDescription => TimeSpan.FromSeconds(DurationSeconds).ToString(@"m\:ss");
}

public interface ILyricSearchService
{
    Task<IReadOnlyList<OnlineLyricResult>> SearchAsync(LyricSearchQuery query, CancellationToken cancellationToken = default);
}
