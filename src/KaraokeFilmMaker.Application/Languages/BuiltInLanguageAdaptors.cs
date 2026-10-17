using System.Text;
using System.Text.RegularExpressions;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Japanese;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Languages;

public sealed class ChineseLanguageAdaptor : BaseLanguageAdaptor
{
    public override string LanguageId => LanguageIds.Chinese;
    public override string DisplayName => "中文";
    public override BaseLanguagePolicy Policy { get; } = new ChineseLanguagePolicy();
}
public sealed class EnglishLanguageAdaptor : BaseLanguageAdaptor
{
    public override string LanguageId => LanguageIds.English;
    public override string DisplayName => "English / 英语";
    public override BaseLanguagePolicy Policy { get; } = new EnglishLanguagePolicy();
}
public sealed class JapaneseLanguageAdaptor(IJapaneseReadingService readingService) : BaseLanguageAdaptor
{
    public override string LanguageId => LanguageIds.Japanese;
    public override string DisplayName => "日本語 / 日语";
    public override LanguageCapabilities Capabilities => new(true, true, SupportsManualReading: true);
    public override BaseLanguagePolicy Policy { get; } = new JapaneseLanguagePolicy(readingService);
}

public sealed class ChineseLanguagePolicy : BaseLanguagePolicy
{
    internal static readonly string[] DistinctiveWords = ["我们", "我們", "你们", "你們", "什么", "什麼", "没有", "沒有", "这个", "這個", "一起", "喜欢", "喜歡", "爱你", "愛你", "我的", "你的", "你好", "这是", "這是", "不会", "不會"];
    public override IReadOnlyList<int> ProposeBoundaries(LanguageDetectionContext context)
    {
        if (!context.Text.EnumerateRunes().Any(ScriptFacts.IsKana)) { return []; }
        List<int> boundaries = [];
        foreach (string word in DistinctiveWords)
        {
            int cursor = 0;
            while (cursor < context.Text.Length)
            {
                int index = context.Text.IndexOf(word, cursor, StringComparison.Ordinal);
                if (index < 0) { break; }
                boundaries.Add(index); boundaries.Add(index + word.Length); cursor = index + word.Length;
            }
        }
        return boundaries.Distinct().Order().ToArray();
    }
    public override LanguageEvidence Detect(LanguageDetectionContext context)
    {
        if (context.Text.EnumerateRunes().Any(ScriptFacts.IsKana)) { return new(0, "包含假名"); }
        if (!context.Text.EnumerateRunes().Any(ScriptFacts.IsHan)) { return new(0, "无汉字证据"); }
        if (DistinctiveWords.Any(word => context.Text.Contains(word, StringComparison.Ordinal)))
        { return new(90, "中文词语证据"); }
        if (context.Text.IndexOfAny("这们没吗呢吧着说让爱梦欢为会还来时风听吗东".ToCharArray()) >= 0)
        { return new(85, "中文常用字形与功能词"); }
        return new(10, "汉字本身不能区分中日文");
    }
    public override Task<LanguageAnalysisResult> AnalyzeAsync(LanguageAnalysisContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(LanguagePresentation.AnalyzePlain(context, LyricLanguage.Chinese, LanguageIds.Chinese));
}

public sealed class EnglishLanguagePolicy : BaseLanguagePolicy
{
    private static readonly HashSet<string> Words = new(("a an the i you he she it we they me my your his her our their us them "
        + "is am are was were be been being do does did have has had can could will would shall should may might must "
        + "and or but if as in on at by for with from to of into out up down over under not no yes oh hey hello goodbye "
        + "love loved loving baby heart dream dreams night day light world sing song music dance dancing time life live "
        + "never always forever together tonight want need know go come stay believe let don't can't won't I'm you're "
        + "beautiful happy star stars sky rain sun moon soul one two three all some any nothing everything every again "
        + "when where why how what who that this these those so just still only really feel see look run away back").Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
    public override LanguageEvidence Detect(LanguageDetectionContext context)
    {
        var words = Regex.Matches(context.Text, @"[\p{L}\p{M}]+(?:['’][\p{L}\p{M}]+)*").Select(match => match.Value).ToArray();
        if (words.Length == 0 || words.Any(word => word.Any(character => character > 127 && character != '’')))
        { return new(0, "拉丁字母不等同于英语"); }
        int recognized = words.Count(word => Words.Contains(word.Replace('’', '\'')));
        return recognized > 0 && recognized * 2 >= words.Length
            ? new(85, "英语常用词证据") : new(5, "缺少英语词语证据");
    }
    public override Task<LanguageAnalysisResult> AnalyzeAsync(LanguageAnalysisContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(LanguagePresentation.AnalyzePlain(context, LyricLanguage.English, LanguageIds.English));
}

public sealed class JapaneseLanguagePolicy(IJapaneseReadingService readingService) : BaseLanguagePolicy
{
    internal static bool HasUnresolvedReading(LyricUnit unit) => string.IsNullOrWhiteSpace(unit.Reading)
        || unit.Reading.EnumerateRunes().Any(rune => ScriptFacts.IsHan(rune)
            || rune.Value is 0x3095 or 0x3096 or 0x30F5 or 0x30F6
            || (Rune.IsLetter(rune) && !ScriptFacts.IsKana(rune) && rune.Value != 0x30FC));
    internal static bool HasUncoveredKanji(LyricLine line, TextRange range)
    {
        int position = range.Start;
        foreach (Rune rune in line.PrimaryText[range.Start..range.EndExclusive].EnumerateRunes())
        {
            if (ScriptFacts.IsHan(rune) && !line.Units.Any(unit => unit.DisplayRanges.Any(mapping =>
                mapping.Start <= position && mapping.EndExclusive >= position + rune.Utf16SequenceLength)))
            { return true; }
            position += rune.Utf16SequenceLength;
        }
        return false;
    }
    public override string? GetRecordingIssue(LyricLine line, LyricLanguageSpan span) =>
        line.Units.Any(HasUnresolvedReading) ? "日语读音尚未完整，请填写假名而非罗马音。"
            : HasUncoveredKanji(line, span.Range) ? "日语汉字尚未全部建立读音映射，请补齐假名。" : null;
    public override LanguageEvidence Detect(LanguageDetectionContext context)
    {
        if (context.Text.EnumerateRunes().Any(ScriptFacts.IsKana)) { return new(100, "假名及相邻汉字"); }
        if (context.Text.EnumerateRunes().Any(ScriptFacts.IsHan))
        {
            if (context.Text.IndexOfAny("駅僕俺咲込峠畑辻働涙".ToCharArray()) >= 0) { return new(85, "日语词形证据"); }
            return new(10, "孤立汉字存在语言歧义");
        }
        return new(0, "无日文证据");
    }
    public override async Task<LanguageAnalysisResult> AnalyzeAsync(LanguageAnalysisContext context, CancellationToken cancellationToken = default)
    {
        var line = LanguagePresentation.CreateLine(context);
        line = context.ManualReading is { Length: > 0 }
            ? JapaneseAnnotationService.ApplyManualReading(line, context.ManualReading)
            : await new JapaneseAnnotationService(readingService).AnnotateLineAsync(line, cancellationToken).ConfigureAwait(false);
        line = line with { Units = line.Units.Select(unit => unit with { LanguageId = LanguageIds.Japanese }).ToArray() };
        var presentation = CreatePresentation(line);
        bool unresolved = line.Units.Any(HasUnresolvedReading);
        return new(line.Units, presentation.RecordingCells, presentation.SubtitleRuns, unresolved,
            unresolved ? "日语读音尚未完整，请校正假名（罗马音不能替代假名）。" : null,
            line.ReadingText, line.RomanizationText, presentation.RomanizedSubtitleRuns);
    }
    public override LyricPresentationSnapshot CreatePresentation(LyricLine line) => LanguagePresentation.BuildJapanese(line);
}

internal static class ScriptFacts
{
    public static bool IsHan(Rune rune) => rune.Value is >= 0x3400 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF;
    public static bool IsKana(Rune rune) => rune.Value is >= 0x3041 and <= 0x3096 or >= 0x30A1 and <= 0x30FA or >= 0xFF66 and <= 0xFF9D;
    public static int Script(string text)
    {
        Rune rune = text.EnumerateRunes().First();
        if (IsHan(rune) || IsKana(rune) || rune.Value == 0x30FC) { return 1; }
        if (Rune.IsLetter(rune) && rune.Value is <= 0x024F or >= 0x1E00 and <= 0x1EFF) { return 2; }
        return Rune.IsLetter(rune) ? 3 : 0;
    }
}
