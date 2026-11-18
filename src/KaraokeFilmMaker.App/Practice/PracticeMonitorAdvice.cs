using System.Text.RegularExpressions;
using KaraokeFilmMaker.Application.Practice;

namespace KaraokeFilmMaker.App.Practice;

internal sealed record PracticeMonitorRoute(PracticeAudioDevice Input, PracticeAudioDevice Output, int Rank, string Label, string Advice)
{
    public string Display => $"{Label} · {Input.Name} → {Output.Name}";
}

internal static class PracticeMonitorAdvice
{
    public const string Unknown = "未识别到可配对的监听端口。如果设备带 Direct Monitor，可将耳机接到设备试听，保持软件耳返关闭。";

    // See docs/PRACTICE_MONITOR_DEVICES.md for the manufacturer sources.
    private static readonly (string Pattern, string Advice)[] Models =
    [
        (@"VT[-\s]?[34]", "麦克风接 MIC IN，耳机接 PHONES。"),
        (@"Rubix\s?(?:22|24|44)", "耳机接声卡，使用机身 DIRECT MONITOR 调节输入监听。"),
        (@"AG(?:03|06)\s?MK2|AG01", "耳机接设备，关闭 MIX MINUS，使麦克风进入耳机混音。"),
        (@"Scarlett\s+(?:Solo|2i2|4i4|8i6|18i8|18i20|16i16|18i16)", "耳机接声卡，通过面板或对应版本的 Focusrite Control 将输入混入耳机输出。"),
        (@"MOTU\s+M[246]", "耳机接声卡，按下麦克风通道的 MON 键。")
    ];

    private static string Description(PracticeAudioDevice device) => $"{device.Adapter} {device.Name}";

    private static (string Name, string Advice)? Identify(PracticeAudioDevice device)
    {
        foreach (var (pattern, advice) in Models)
        {
            var match = Regex.Match(Description(device), $@"(?<![a-z0-9])(?:{pattern})(?![a-z0-9])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success) return (Regex.Replace(match.Value, @"[\s-]", "").ToUpperInvariant(), advice);
        }
        return null;
    }

    private static bool Realtek(PracticeAudioDevice d) => Description(d).Contains("Realtek", StringComparison.OrdinalIgnoreCase);
    private static bool Analog(PracticeAudioDevice d, bool input)
    {
        if (Regex.IsMatch(d.Name, @"HDMI|S/?PDIF|Digital|数字|數位|Loopback|Stereo Mix|立体声混音", RegexOptions.IgnoreCase)) return false;
        return d.FormFactor switch
        {
            null or 10 => Regex.IsMatch(d.Name, input ? @"mic|麦克风|line in|线路输入" : @"headphone|speaker|耳机|扬声器|音箱|line out", RegexOptions.IgnoreCase),
            2 or 5 => true, // Line-level or headset
            4 => input,
            1 or 3 => !input,
            _ => false
        };
    }

    public static PracticeMonitorRoute[] Find(IReadOnlyList<PracticeAudioDevice> inputs, IReadOnlyList<PracticeAudioDevice> outputs)
    {
        var routes = new List<PracticeMonitorRoute>();
        foreach (var input in inputs)
        foreach (var output in outputs)
        {
            bool sameParent = input.Parent != null && string.Equals(input.Parent, output.Parent, StringComparison.OrdinalIgnoreCase);
            bool sameContainer = input.Container.HasValue && input.Container == output.Container;
            if (input.Container.HasValue && output.Container.HasValue && !sameContainer) continue;
            if (input.Parent != null && output.Parent != null && !sameContainer && !sameParent) continue;
            bool paired = sameParent || sameContainer;
            var a = Identify(input);
            var b = Identify(output);
            if (a.HasValue && b.HasValue && a.Value.Name != b.Value.Name) continue;
            var model = a ?? b;
            if (model.HasValue && (paired || (a.HasValue && b.HasValue)))
            {
                string label = paired ? "推荐硬件直听" : "型号匹配，请核对实物";
                routes.Add(new(input, output, paired ? 0 : 2, label, model.Value.Advice));
            }
            else if (Realtek(input) && Realtek(output) && Analog(input, true) && Analog(output, false))
            {
                // Without physical identity leave selection to the user; never claim analog bypass.
                routes.Add(new(input, output, paired ? 3 : 4, paired ? "可试 Realtek 监听" : "Realtek 名称匹配，请核对插口",
                    "将麦克风和耳机接到同一声卡，在 Realtek 控制台检查麦克风回放音量。部分主板没有开放此通路。"));
            }
            else if (paired && !Realtek(input) && !Realtek(output) &&
                     (input.Parent?.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) == true ||
                      output.Parent?.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) == true))
            {
                routes.Add(new(input, output, 1, "可试 USB 设备监听", "检查设备是否带耳机口及 Direct Monitor 或 Input/Playback 控制，接上耳机试听。"));
            }
        }
        return routes.OrderBy(route => route.Rank).ThenBy(route => route.Input.Name.Contains("BYPASS", StringComparison.OrdinalIgnoreCase))
            .ThenBy(route => route.Output.Name).ToArray();
    }
}
