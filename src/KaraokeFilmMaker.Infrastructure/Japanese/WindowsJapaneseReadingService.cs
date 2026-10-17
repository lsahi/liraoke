using System.Runtime.Versioning;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Japanese;
using Windows.Globalization;

namespace KaraokeFilmMaker.Infrastructure.Japanese;

[SupportedOSPlatform("windows10.0.17763")]
public sealed class WindowsJapaneseReadingService : IJapaneseReadingService
{
    private const int MaximumAnalyzerLength = 100;

    public async Task<IReadOnlyList<JapaneseToken>> AnalyzeAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<IReadOnlyList<JapaneseToken>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(AnalyzeCore(text, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Japanese phonetic analyzer",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await completion.Task.ConfigureAwait(false);
    }

    private static IReadOnlyList<JapaneseToken> AnalyzeCore(
        string text,
        CancellationToken cancellationToken)
    {
        List<JapaneseToken> result = [];
        foreach ((string Chunk, int Offset) in SplitInput(text))
        {
            IReadOnlyList<JapanesePhoneme> phonemes = JapanesePhoneticAnalyzer.GetWords(Chunk, false);
            int searchStart = 0;
            foreach (JapanesePhoneme phoneme in phonemes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int localStart = Chunk.IndexOf(phoneme.DisplayText, searchStart, StringComparison.Ordinal);
                if (localStart < 0)
                {
                    localStart = searchStart;
                }

                string reading = KanaRomanizer.ToHiragana(phoneme.YomiText);
                result.Add(new JapaneseToken(
                    phoneme.DisplayText,
                    reading,
                    KanaRomanizer.Romanize(reading),
                    Offset + localStart,
                    phoneme.DisplayText.Length,
                    RequiresReview: ContainsKanji(phoneme.DisplayText)));
                searchStart = localStart + phoneme.DisplayText.Length;
            }
        }

        return result;
    }

    private static IEnumerable<(string Chunk, int Offset)> SplitInput(string text)
    {
        for (var offset = 0; offset < text.Length;)
        {
            int length = Math.Min(MaximumAnalyzerLength, text.Length - offset);
            if (length > 0 && offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]))
            {
                length--;
            }

            if (length == 0)
            {
                length = 1;
            }

            yield return (text.Substring(offset, length), offset);
            offset += length;
        }
    }

    private static bool ContainsKanji(string text) =>
        text.Any(character => character is >= '\u3400' and <= '\u9FFF');
}
