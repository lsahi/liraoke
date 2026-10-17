using System.Globalization;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Lyrics;

/// <summary>Edits a unit's source-text mapping, without editing the source text or its recorded times.</summary>
public static class LyricTextMapping
{
    public static IReadOnlyList<TextRange> Parse(string source, string value)
    {
        var ranges = value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            string[] pair = part.Trim().Split('-');
            if (pair.Length != 2 || !int.TryParse(pair[0], out int start) || !int.TryParse(pair[1], out int end)
                || start <= 0 || end < start || end > source.Length)
            { throw new ArgumentException("文字范围从 1 开始，例如 9-9；不能倒序或超过本句长度。"); }
            return new TextRange(start - 1, end - start + 1);
        }).ToArray();
        var boundaries = StringInfo.ParseCombiningCharacters(source).Append(source.Length).ToHashSet();
        if (ranges.Length == 0 || ranges.Any(range => !boundaries.Contains(range.Start) || !boundaries.Contains(range.EndExclusive))
            || ranges.Zip(ranges.Skip(1)).Any(pair => pair.First.EndExclusive > pair.Second.Start))
        { throw new ArgumentException("请按原文顺序选择完整文字，范围不能为空、重叠或切断组合字符。"); }
        return ranges;
    }

    public static IReadOnlyList<TextRange> Resolve(string source, string text, IReadOnlyList<TextRange> current)
    {
        if (string.IsNullOrWhiteSpace(text)) { throw new ArgumentException("对应歌词不能为空，请填写本句中已有的文字。"); }
        var boundaries = StringInfo.ParseCombiningCharacters(source).Append(source.Length).ToHashSet();
        var matches = new List<TextRange>();
        for (int offset = 0; offset <= source.Length - text.Length; offset++)
        {
            if (boundaries.Contains(offset) && boundaries.Contains(offset + text.Length)
                && source.AsSpan(offset, text.Length).SequenceEqual(text.AsSpan()))
            { matches.Add(new TextRange(offset, text.Length)); }
        }
        var local = matches.Where(match => current.Any(range => match.Start >= range.Start && match.EndExclusive <= range.EndExclusive)).ToArray();
        if (local.Length == 1) { return local; }
        if (local.Length == 0 && matches.Count == 1) { return matches; }
        throw new ArgumentException(matches.Count == 0
            ? "请填写本句中已有的文字。这里只修改对应范围；修改原文请使用歌词编辑。"
            : "本句中有多处相同文字，请在“文字范围”中明确位置，例如 9-9。");
    }

    public static string Format(IReadOnlyList<TextRange> ranges) =>
        string.Join(",", ranges.Select(range => $"{range.Start + 1}-{range.EndExclusive}"));
}
