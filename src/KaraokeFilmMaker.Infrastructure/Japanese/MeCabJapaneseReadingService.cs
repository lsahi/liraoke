using System.Text;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Japanese;
using NMeCab.Specialized;

namespace KaraokeFilmMaker.Infrastructure.Japanese;

public sealed class MeCabJapaneseReadingService : IJapaneseReadingService, IDisposable
{
    private readonly object _gate = new();
    private MeCabIpaDicTagger? _tagger;

    public Task<IReadOnlyList<JapaneseToken>> AnalyzeAsync(string text, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<JapaneseToken>>(() =>
        {
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _tagger ??= MeCabIpaDicTagger.Create(Path.Combine(AppContext.BaseDirectory, "IpaDic"));
                List<JapaneseToken> tokens = [];
                int cursor = 0;
                foreach (var node in _tagger.Parse(text))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int start = text.IndexOf(node.Surface, cursor, StringComparison.Ordinal);
                    if (start < 0) { start = cursor; }
                    if (start > cursor) { AddGap(cursor, start); }
                    string reading = KanaRomanizer.ToHiragana(string.IsNullOrWhiteSpace(node.Reading) || node.Reading == "*" ? node.Surface : node.Reading);
                    string romaji = KanaRomanizer.Romanize(reading);
                    if (node.PartsOfSpeech == "助詞")
                    {
                        romaji = node.Surface switch { "へ" => "e", "は" => "wa", "を" => "o", _ => romaji };
                    }
                    tokens.Add(new JapaneseToken(node.Surface, reading, romaji, start, node.Surface.Length,
                        ContainsKanji(node.Surface)));
                    cursor = start + node.Surface.Length;
                }
                if (cursor < text.Length) { AddGap(cursor, text.Length); }
                return tokens;

                void AddGap(int start, int end)
                {
                    string gap = text[start..end];
                    tokens.Add(new JapaneseToken(gap, gap, gap, start, gap.Length, false));
                }
            }
        }, cancellationToken);

    public static bool ContainsKanji(string text) => text.EnumerateRunes().Any(rune =>
        rune.Value is >= 0x3400 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF);

    public void Dispose()
    {
        lock (_gate) { _tagger?.Dispose(); _tagger = null; }
    }
}
