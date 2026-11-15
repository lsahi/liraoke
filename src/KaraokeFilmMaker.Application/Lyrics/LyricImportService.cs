using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Application.Japanese;
using KaraokeFilmMaker.Application.Abstractions;

namespace KaraokeFilmMaker.Application.Lyrics;

public sealed class LyricImportService
{
    private static readonly Regex LrcTimestamp = new(
        @"\[(?<minute>\d{1,3}):(?<second>\d{1,2})(?:[\.:](?<fraction>\d{1,3}))?\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SrtTimestamp = new(
        @"^(?<start>\d{1,2}:\d{2}:\d{2}[,.]\d{3})\s*-->\s*(?<end>\d{1,2}:\d{2}:\d{2}[,.]\d{3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string AttachedKana = "ゃゅょャュョぁぃぅぇぉァィゥェォゎヮっッんンー";
    private static readonly Regex LatinWord = new(
        @"[A-Za-z\u00C0-\u024F][A-Za-z\u00C0-\u024F\p{M}]*(?:['’\-][A-Za-z\u00C0-\u024F][A-Za-z\u00C0-\u024F\p{M}]*)*|\d+(?:[.,]\d+)*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<LyricDocument> ImportCopyAsync(
        string sourcePath,
        string workspacePath,
        LyricLanguage language,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        string directory = Path.Combine(Path.GetFullPath(workspacePath), "lyrics");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory,
            $"{Path.GetFileNameWithoutExtension(sourcePath)}-{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");
        bool destinationCreated = false;
        try
        {
            await using (FileStream source = File.OpenRead(sourcePath))
            await using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16 * 1024, useAsync: true))
            {
                destinationCreated = true;
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            return await ImportAsync(destination, language, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (destinationCreated)
            {
                File.Delete(destination);
            }

            throw;
        }
    }

    public async Task<LyricDocument> ImportAsync(
        string filePath,
        LyricLanguage language,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string text = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        return Parse(text, Path.GetExtension(filePath), language);
    }

    public LyricDocument Parse(string text, string extension, LyricLanguage language)
    {
        ArgumentNullException.ThrowIfNull(text);
        string normalizedExtension = extension.Trim().ToLowerInvariant();
        bool marked = SplitLines(text).Any(line => line.Trim().TrimStart('\uFEFF') == AnnotatedLyricText.SourceMarker);
        if (marked && normalizedExtension is ".txt" or "") { normalizedExtension = ".lrc"; }

        IReadOnlyList<ParsedLine> parsedLines = normalizedExtension switch
        {
            ".lrc" => ParseLrc(text),
            ".srt" => ParseSrt(text),
            ".txt" or "" => ParsePlainText(text),
            _ => throw new NotSupportedException($"不支持的歌词格式：{extension}"),
        };

        var lines = parsedLines
            .Select((line, index) =>
            {
                try { return CreateLine(line, language); }
                catch (FormatException error)
                { throw new FormatException($"第 {index + 1} 句：{error.Message}", error); }
            })
            .ToArray();
        return new LyricDocument(language, lines);
    }

    private static IReadOnlyList<ParsedLine> ParsePlainText(string text)
    {
        return SplitLines(text)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line => new ParsedLine(line, ProjectTime.Zero, ProjectTime.Zero))
            .ToArray();
    }

    private static IReadOnlyList<ParsedLine> ParseLrc(string text)
    {
        List<(ProjectTime Start, string Text)> entries = [];
        List<string> untimed = [];
        bool annotated = AnnotatedLyricText.HasMarkup(text) || text.Contains(AnnotatedLyricText.SourceMarker, StringComparison.Ordinal);

        foreach (string sourceLine in SplitLines(text))
        {
            MatchCollection matches = LrcTimestamp.Matches(sourceLine);
            if (matches.Count == 0)
            {
                string plain = sourceLine.Trim().TrimStart('\uFEFF');
                if (annotated && plain.Length > 0 && !Regex.IsMatch(plain, @"^\[[A-Za-z][A-Za-z0-9_-]*:.*\]$"))
                { untimed.Add(plain); }
                continue;
            }

            string lyricText = LrcTimestamp.Replace(sourceLine, string.Empty).Trim();
            if (lyricText.Length == 0)
            {
                continue;
            }

            foreach (Match match in matches)
            {
                entries.Add((ParseLrcTime(match), lyricText));
            }
        }

        if (untimed.Count > 0)
        {
            if (entries.Count > 0)
            { throw new FormatException("带注音 LRC 同时包含有时间和无时间的歌词行；请补齐行时间，或移除全部时间后导入，避免遗漏歌词。"); }
            return untimed.Select(line => new ParsedLine(line, ProjectTime.Zero, ProjectTime.Zero)).ToArray();
        }

        var ordered = entries.OrderBy(entry => entry.Start).ToArray();
        List<ParsedLine> result = [];
        for (var index = 0; index < ordered.Length; index++)
        {
            ProjectTime start = ordered[index].Start;
            ProjectTime end = index + 1 < ordered.Length
                ? ordered[index + 1].Start
                : start + ProjectDuration.FromSeconds(5);
            if (end < start)
            {
                end = start;
            }

            result.Add(new ParsedLine(ordered[index].Text, start, end));
        }

        return result;
    }

    private static IReadOnlyList<ParsedLine> ParseSrt(string text)
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] blocks = Regex.Split(normalized.Trim(), @"\n\s*\n");
        List<ParsedLine> result = [];

        foreach (string block in blocks)
        {
            string[] lines = block.Split('\n');
            int timingIndex = Array.FindIndex(lines, line => SrtTimestamp.IsMatch(line.Trim()));
            if (timingIndex < 0)
            {
                continue;
            }

            Match match = SrtTimestamp.Match(lines[timingIndex].Trim());
            string lyricText = string.Join(" ", lines.Skip(timingIndex + 1).Select(line => line.Trim()))
                .Trim();
            if (lyricText.Length == 0)
            {
                continue;
            }

            result.Add(new ParsedLine(
                lyricText,
                ParseSrtTime(match.Groups["start"].Value),
                ParseSrtTime(match.Groups["end"].Value)));
        }

        return result.OrderBy(line => line.Start).ToArray();
    }

    private static LyricLine CreateLine(ParsedLine parsed, LyricLanguage language)
    {
        AnnotatedLyricText annotation = AnnotatedLyricText.Parse(parsed.Text);
        parsed = parsed with { Text = annotation.Text };
        TimeRange lineTiming = new(parsed.Start, parsed.End);
        IReadOnlyList<LyricUnit> units = CreateUnits(parsed.Text, lineTiming, language);
        if (annotation.Readings.Count > 0)
        {
            List<JapaneseToken> tokens = [];
            int cursor = 0;
            foreach (var span in annotation.Readings)
            {
                if (span.Range.Start > cursor) { AddToken(cursor, span.Range.Start - cursor, null); }
                AddToken(span.Range.Start, span.Range.Length, span.ManualReading);
                cursor = span.Range.EndExclusive;
            }
            if (cursor < parsed.Text.Length) { AddToken(cursor, parsed.Text.Length - cursor, null); }
            units = JapaneseAnnotationService.BuildUnits(tokens, lineTiming);

            void AddToken(int start, int length, string? reading)
            {
                string surface = parsed.Text.Substring(start, length);
                tokens.Add(new(surface, reading ?? surface, KanaRomanizer.Romanize(reading ?? surface), start, length, reading is null));
            }
        }
        return new LyricLine(
            Guid.NewGuid(),
            parsed.Text,
            AnnotationType.None,
            AnnotationText: null,
            TranslationText: null,
            lineTiming,
            units,
            ReviewState.Unreviewed,
            ReadingSource: null)
        { LanguageSpans = annotation.Readings.Count > 0 ? annotation.Readings : null };
    }

    public static IReadOnlyList<LyricUnit> CreateUnits(
        string text,
        TimeRange lineTiming,
        LyricLanguage language)
    {
        List<(int Start, int Length, string Text)> elements = [];
        var words = LatinWord.Matches(text).ToDictionary(match => match.Index);
        int consumedUntil = 0;
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            string element = enumerator.GetTextElement();
            int index = enumerator.ElementIndex;
            if (index < consumedUntil) { continue; }
            if (words.TryGetValue(index, out Match? word))
            {
                elements.Add((index, word.Length, word.Value));
                consumedUntil = index + word.Length;
                continue;
            }
            if (!ShouldCreateUnit(element))
            {
                continue;
            }

            if (language == LyricLanguage.Japanese
                && AttachedKana.Contains(element, StringComparison.Ordinal)
                && elements.Count > 0 && elements[^1].Start + elements[^1].Length == index
                && elements[^1].Text.Any(character => character is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヶ'))
            {
                var previous = elements[^1];
                elements[^1] = (previous.Start, previous.Length + element.Length, previous.Text + element);
            }
            else
            {
                elements.Add((index, element.Length, element));
            }
        }

        List<LyricUnit> units = [];
        long totalDuration = lineTiming.Duration.Microseconds;
        for (var index = 0; index < elements.Count; index++)
        {
            long startOffset = elements.Count == 0 ? 0 : totalDuration * index / elements.Count;
            long endOffset = elements.Count == 0 ? 0 : totalDuration * (index + 1) / elements.Count;
            var timing = new TimeRange(
                new ProjectTime(lineTiming.Start.Microseconds + startOffset),
                new ProjectTime(lineTiming.Start.Microseconds + endOffset));
            var element = elements[index];
            units.Add(new LyricUnit(
                Guid.NewGuid(),
                [new TextRange(element.Start, element.Length)],
                element.Text,
                Romanization: language == LyricLanguage.Japanese
                    ? KanaRomanizer.RomanizeSingingUnit(element.Text, index + 1 < elements.Count ? elements[index + 1].Text : null) : null,
                timing,
                RawInputTime: null,
                IsConfirmed: false));
        }

        return units;
    }

    private static bool ShouldCreateUnit(string element)
    {
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(element, 0);
        return category is not (
            UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.SpaceSeparator
            or UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation);
    }

    private static ProjectTime ParseLrcTime(Match match)
    {
        int minutes = int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture);
        int seconds = int.Parse(match.Groups["second"].Value, CultureInfo.InvariantCulture);
        string fractionText = match.Groups["fraction"].Value;
        int milliseconds = fractionText.Length switch
        {
            0 => 0,
            1 => int.Parse(fractionText, CultureInfo.InvariantCulture) * 100,
            2 => int.Parse(fractionText, CultureInfo.InvariantCulture) * 10,
            _ => int.Parse(fractionText[..3], CultureInfo.InvariantCulture),
        };

        return ProjectTime.FromMilliseconds(((minutes * 60L) + seconds) * 1_000L + milliseconds);
    }

    private static ProjectTime ParseSrtTime(string value)
    {
        string normalized = value.Replace(',', '.');
        TimeSpan parsed = TimeSpan.ParseExact(normalized, @"h\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        return new ProjectTime(checked(parsed.Ticks / 10));
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private sealed record ParsedLine(string Text, ProjectTime Start, ProjectTime End);
}
