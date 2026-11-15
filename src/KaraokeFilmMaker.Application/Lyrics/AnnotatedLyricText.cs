using System.Globalization;
using System.Text;
using KaraokeFilmMaker.Application.Japanese;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Lyrics;

/// <summary>Offline import syntax. Offsets refer to the decoded lyric, never the markup.</summary>
public sealed record AnnotatedLyricText(string Text, IReadOnlyList<LyricLanguageSpan> Readings)
{
    public const string SourceMarker = "[tool:utaten-ruby]";

    public static bool HasMarkup(string text) => text.Contains('{') && text.Contains("||", StringComparison.Ordinal);

    public static AnnotatedLyricText Parse(string source)
    {
        var text = new StringBuilder();
        List<LyricLanguageSpan> readings = [];
        int cursor = 0;
        while (cursor < source.Length)
        {
            int open = source.IndexOf('{', cursor);
            if (open < 0) { text.Append(source.AsSpan(cursor)); break; }
            text.Append(source.AsSpan(cursor, open - cursor));
            int close = source.IndexOf('}', open + 1);
            int separator = source.IndexOf("||", open + 1, StringComparison.Ordinal);
            if (separator < 0 || (close >= 0 && separator > close))
            {
                text.Append('{'); cursor = open + 1; continue;
            }
            if (close < 0) { throw Invalid("缺少结束符 }"); }
            string surface = source[(open + 1)..separator];
            string reading = source[(separator + 2)..close];
            if (string.IsNullOrWhiteSpace(surface) || surface.IndexOfAny(['{', '}', '|']) >= 0)
            { throw Invalid("原文不能为空，也不能包含嵌套标记"); }
            int start = text.Length;
            text.Append(surface);
            string[] groups = reading.Split(',');
            int[] boundaries = StringInfo.ParseCombiningCharacters(surface).Append(surface.Length).ToArray();
            if (groups.Length == 1)
            { Add(start, surface.Length, reading); }
            else
            {
                if (groups.Length != boundaries.Length - 1 || string.IsNullOrWhiteSpace(groups[0]))
                { throw Invalid("按字读音的逗号分组数必须等于原文字数，首组不能为空"); }
                // Empty entries continue the preceding word (e.g. 今日||きょう,).
                for (int i = 0; i < groups.Length;)
                {
                    int end = i + 1;
                    while (end < groups.Length && groups[end].Length == 0) { end++; }
                    Add(start + boundaries[i], boundaries[end] - boundaries[i], groups[i]);
                    i = end;
                }
            }
            cursor = close + 1;
        }
        string decoded = text.ToString();
        var legal = StringInfo.ParseCombiningCharacters(decoded).Append(decoded.Length).ToHashSet();
        if (readings.Any(span => !legal.Contains(span.Range.Start) || !legal.Contains(span.Range.EndExclusive)))
        { throw Invalid("注音边界不能拆开组合字符"); }
        return new(decoded, readings);

        void Add(int start, int length, string value)
        {
            string kana = KanaRomanizer.ToHiragana(value.Trim().Normalize(NormalizationForm.FormKC));
            if (kana.Length == 0 || kana.Any(c => !(c is >= '\u3041' and <= '\u3096' or '\u3099' or '\u309A' or '\u309D' or '\u309E' or 'ー')))
            { throw Invalid("读音须为非空假名；当前不支持 | 分拍、罗马音或汉字读音"); }
            readings.Add(new(Guid.NewGuid(), new(start, length), LanguageIds.Japanese,
                LanguageDetectionSource.Manual, IsLocked: true, ManualReading: kana));
        }
    }

    private static FormatException Invalid(string reason) => new($"带注音歌词格式错误：{reason}。请使用 {{原文||かな}}。");
}
