using System.Globalization;
using System.Text;

namespace KaraokeFilmMaker.Application.Japanese;

public static class KanaRomanizer
{
    private const string SmallKana = "ゃゅょぁぃぅぇぉゎャュョァィゥェォヮ";

    private static readonly IReadOnlyDictionary<string, string> Romanization =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["あ"] = "a", ["い"] = "i", ["う"] = "u", ["え"] = "e", ["お"] = "o",
            ["か"] = "ka", ["き"] = "ki", ["く"] = "ku", ["け"] = "ke", ["こ"] = "ko",
            ["さ"] = "sa", ["し"] = "shi", ["す"] = "su", ["せ"] = "se", ["そ"] = "so",
            ["た"] = "ta", ["ち"] = "chi", ["つ"] = "tsu", ["て"] = "te", ["と"] = "to",
            ["な"] = "na", ["に"] = "ni", ["ぬ"] = "nu", ["ね"] = "ne", ["の"] = "no",
            ["は"] = "ha", ["ひ"] = "hi", ["ふ"] = "fu", ["へ"] = "he", ["ほ"] = "ho",
            ["ま"] = "ma", ["み"] = "mi", ["む"] = "mu", ["め"] = "me", ["も"] = "mo",
            ["や"] = "ya", ["ゆ"] = "yu", ["よ"] = "yo",
            ["ら"] = "ra", ["り"] = "ri", ["る"] = "ru", ["れ"] = "re", ["ろ"] = "ro",
            ["わ"] = "wa", ["を"] = "o", ["ん"] = "n",
            ["が"] = "ga", ["ぎ"] = "gi", ["ぐ"] = "gu", ["げ"] = "ge", ["ご"] = "go",
            ["ざ"] = "za", ["じ"] = "ji", ["ず"] = "zu", ["ぜ"] = "ze", ["ぞ"] = "zo",
            ["だ"] = "da", ["ぢ"] = "ji", ["づ"] = "zu", ["で"] = "de", ["ど"] = "do",
            ["ば"] = "ba", ["び"] = "bi", ["ぶ"] = "bu", ["べ"] = "be", ["ぼ"] = "bo",
            ["ぱ"] = "pa", ["ぴ"] = "pi", ["ぷ"] = "pu", ["ぺ"] = "pe", ["ぽ"] = "po",
            ["ゔ"] = "vu",
            ["きゃ"] = "kya", ["きゅ"] = "kyu", ["きょ"] = "kyo",
            ["しゃ"] = "sha", ["しゅ"] = "shu", ["しょ"] = "sho",
            ["ちゃ"] = "cha", ["ちゅ"] = "chu", ["ちょ"] = "cho",
            ["にゃ"] = "nya", ["にゅ"] = "nyu", ["にょ"] = "nyo",
            ["ひゃ"] = "hya", ["ひゅ"] = "hyu", ["ひょ"] = "hyo",
            ["みゃ"] = "mya", ["みゅ"] = "myu", ["みょ"] = "myo",
            ["りゃ"] = "rya", ["りゅ"] = "ryu", ["りょ"] = "ryo",
            ["ぎゃ"] = "gya", ["ぎゅ"] = "gyu", ["ぎょ"] = "gyo",
            ["じゃ"] = "ja", ["じゅ"] = "ju", ["じょ"] = "jo",
            ["びゃ"] = "bya", ["びゅ"] = "byu", ["びょ"] = "byo",
            ["ぴゃ"] = "pya", ["ぴゅ"] = "pyu", ["ぴょ"] = "pyo",
            ["ふぁ"] = "fa", ["ふぃ"] = "fi", ["ふぇ"] = "fe", ["ふぉ"] = "fo",
            ["てぃ"] = "ti", ["でぃ"] = "di", ["とぅ"] = "tu", ["どぅ"] = "du",
            ["うぃ"] = "wi", ["うぇ"] = "we", ["うぉ"] = "wo",
            ["ゔぁ"] = "va", ["ゔぃ"] = "vi", ["ゔぇ"] = "ve", ["ゔぉ"] = "vo",
            ["しぇ"] = "she", ["じぇ"] = "je", ["ちぇ"] = "che",
            ["つぁ"] = "tsa", ["つぃ"] = "tsi", ["つぇ"] = "tse", ["つぉ"] = "tso",
            ["てゅ"] = "tyu", ["でゅ"] = "dyu", ["ふゅ"] = "fyu", ["ゔゅ"] = "vyu",
            ["きぇ"] = "kye", ["ぎぇ"] = "gye", ["にぇ"] = "nye", ["ひぇ"] = "hye",
            ["びぇ"] = "bye", ["ぴぇ"] = "pye", ["みぇ"] = "mye", ["りぇ"] = "rye",
            ["くぁ"] = "kwa", ["くぃ"] = "kwi", ["くぇ"] = "kwe", ["くぉ"] = "kwo", ["くゎ"] = "kwa",
            ["ぐぁ"] = "gwa", ["ぐぃ"] = "gwi", ["ぐぇ"] = "gwe", ["ぐぉ"] = "gwo", ["ぐゎ"] = "gwa",
            ["すぃ"] = "si", ["ずぃ"] = "zi", ["ゐ"] = "wi", ["ゑ"] = "we",
        };

    public static IReadOnlyList<string> SplitMora(string reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        string hiragana = ToHiragana(reading);
        List<string> result = [];
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(hiragana);
        while (enumerator.MoveNext())
        {
            string element = enumerator.GetTextElement();
            if (SmallKana.Contains(element, StringComparison.Ordinal) && result.Count > 0)
            {
                result[^1] += element;
            }
            else
            {
                result.Add(element);
            }
        }

        return result;
    }

    // Singing defaults intentionally keep vowel kana (e.g. じょ・う) separate.
    // Nasals, closures, small vowel/y kana and the length mark attach to the preceding onset.
    public static IReadOnlyList<string> SplitSingingUnits(string reading)
    {
        List<string> result = [];
        foreach (string mora in SplitMora(reading.Normalize(NormalizationForm.FormKC)))
        {
            if (result.Count > 0 && (mora is "っ" or "ん" or "ー" || SmallKana.Contains(mora, StringComparison.Ordinal))
                && result[^1].Any(character => character is >= 'ぁ' and <= 'ゖ'))
            {
                result[^1] += mora;
            }
            else { result.Add(mora); }
        }
        return result;
    }

    public static string RomanizeSingingUnit(string reading, string? nextReading = null)
    {
        string kana = ToHiragana(reading);
        string result = Romanize(kana);
        if (kana.EndsWith('っ'))
        {
            string next = Romanize(nextReading ?? "");
            // Display the closure consonant with the preceding unit: is | sho, it | ta.
            result += next.Length > 0 && char.IsAsciiLetter(next[0]) && IsConsonant(next[0])
                ? next[..1] : "(stop)";
        }
        return result;
    }

    public static bool ContainsUnresolvedReading(string text) => text.EnumerateRunes().Any(rune =>
        rune.Value is >= 0x3040 and <= 0x30FF or >= 0x3400 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF or >= 0xFF66 and <= 0xFF9F or >= 0x20000 and <= 0x323AF);

    public static string Romanize(string reading)
    {
        IReadOnlyList<string> mora = SplitMora(reading);
        var result = new StringBuilder();
        bool geminateNext = false;

        foreach (string unit in mora)
        {
            if (unit == "っ")
            {
                geminateNext = true;
                continue;
            }

            if (unit == "ー")
            {
                char? vowel = LastVowel(result);
                if (vowel is not null)
                {
                    result.Append(vowel.Value);
                }

                continue;
            }

            string romanized = Romanization.GetValueOrDefault(unit, unit);
            if (geminateNext && romanized.Length > 0 && IsConsonant(romanized[0]))
            {
                result.Append(romanized[0]);
            }

            result.Append(romanized);
            geminateNext = false;
        }

        return result.ToString();
    }

    public static string ToHiragana(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char character in text)
        {
            builder.Append(character is >= '\u30A1' and <= '\u30F6'
                ? (char)(character - 0x60)
                : character);
        }

        return builder.ToString();
    }

    private static bool IsConsonant(char character) =>
        character is not ('a' or 'e' or 'i' or 'o' or 'u');

    private static char? LastVowel(StringBuilder value)
    {
        for (var index = value.Length - 1; index >= 0; index--)
        {
            if (value[index] is 'a' or 'e' or 'i' or 'o' or 'u')
            {
                return value[index];
            }
        }

        return null;
    }
}
