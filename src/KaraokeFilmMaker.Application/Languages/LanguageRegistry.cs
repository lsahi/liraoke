using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Languages;

public sealed class LanguageRegistry
{
    public LanguageRegistry(IEnumerable<ILanguageAdaptor> adaptors)
    {
        var items = adaptors.ToArray();
        if (items.Any(item => item.ApiVersion != BaseLanguageAdaptor.CurrentApiVersion
            || string.IsNullOrWhiteSpace(item.LanguageId) || item.LanguageId == LanguageIds.Unknown))
        { throw new ArgumentException("语言插件 API 版本不兼容，或语言 ID 无效。"); }
        if (items.GroupBy(item => item.LanguageId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        { throw new ArgumentException("不能注册重复的语言 ID。"); }
        if (items.Any(item => item.Capabilities.TextDirection != LyricTextDirection.LeftToRight))
        { throw new NotSupportedException("当前字幕宿主尚不支持从右向左的文字排版，不能加载该语言包。"); }
        Adaptors = items;
    }
    public IReadOnlyList<ILanguageAdaptor> Adaptors { get; }
    public ILanguageAdaptor? Find(string id) => Adaptors.FirstOrDefault(item =>
        string.Equals(item.LanguageId, id, StringComparison.OrdinalIgnoreCase));
    public static LanguageRegistry CreateBuiltIns(IJapaneseReadingService readingService) => new([
        new ChineseLanguageAdaptor(), new EnglishLanguageAdaptor(), new JapaneseLanguageAdaptor(readingService)]);
}
