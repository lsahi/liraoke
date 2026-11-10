using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Alignment;

public sealed record AlignmentPlugin(string Id, string Name, string License, string ModelUrl, int MinimumRamGiB, int MinimumGpuGiB);
public static class AlignmentPlugins
{
    public static IReadOnlyList<AlignmentPlugin> All { get; } = [
        new("nextfire", "NextFire 日语歌声对齐", "CC BY-NC-SA 4.0；基础模型 CC BY-NC 4.0", "https://huggingface.co/NextFire/mms-300m-ForcedAligner-karaoke-ja-Latn", 8, 4),
        new("qwen", "Qwen3-ForcedAligner-0.6B", "Apache-2.0", "https://huggingface.co/Qwen/Qwen3-ForcedAligner-0.6B-hf", 12, 8)];
    public static AlignmentPlugin Get(string id) => All.SingleOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("未知打轴插件。");
    public static string Preferred(LyricDocument document)
    {
        var counts = document.Lines.Where(l => !l.IsInstrumental).SelectMany(l => l.Units)
            .GroupBy(u => u.LanguageId is "ja" or "en" or "zh" ? u.LanguageId : u.Reading.Any(c => c is >= '\u3040' and <= '\u30ff') ? "ja" : "other")
            .ToDictionary(g => g.Key!, g => g.Count());
        if (counts.Count == 0) return document.Language == LyricLanguage.Japanese ? "nextfire" : "qwen";
        return counts.GetValueOrDefault("ja") > counts.Where(x => x.Key != "ja").Sum(x => x.Value) ? "nextfire" : "qwen";
    }
}
public sealed record AlignmentUnitResult(Guid Id, double Start, double End);
public sealed record AlignmentLineResult(Guid Id, IReadOnlyList<AlignmentUnitResult> Units, string? Error = null);
public sealed record AlignmentOutcome(IReadOnlyList<AlignmentLineResult> Lines, string LogPath);
public sealed record AlignmentApplyResult(LyricDocument Document, int Applied, IReadOnlyList<string> Skipped);

public static class AlignmentEdits
{
    public static bool Protected(LyricLine line) => line.Units.Any(u => u.IsConfirmed || u.RawInputTime is not null);
    public static AlignmentApplyResult Apply(LyricDocument source, AlignmentOutcome output, double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0) throw new InvalidOperationException("歌曲时长无效。");
        if (output.Lines.Select(l => l.Id).Distinct().Count() != output.Lines.Count || output.Lines.Any(l => !source.Lines.Any(s => s.Id == l.Id)))
            throw new InvalidOperationException("插件返回了重复或未知歌词行。");
        var lines = source.Lines.ToArray(); var skipped = new List<string>(); int applied = 0;
        foreach (var result in output.Lines)
        {
            int index = Array.FindIndex(lines, l => l.Id == result.Id); var line = lines[index];
            if (Protected(line)) { skipped.Add($"第 {index+1} 句：保留人工打点。"); continue; }
            if (result.Error is { Length: > 0 }) { skipped.Add($"第 {index+1} 句：{result.Error}"); continue; }
            bool valid = result.Units.Count > 0 && result.Units.Count == line.Units.Count;
            double previous = -1;
            for (int i = 0; valid && i < result.Units.Count; i++)
            {
                var u = result.Units[i]; valid = u.Id == line.Units[i].Id && double.IsFinite(u.Start) && double.IsFinite(u.End)
                    && u.Start >= 0 && u.Start >= previous && u.End > u.Start && u.End <= duration;
                previous = u.End;
            }
            if (!valid) { skipped.Add($"第 {index+1} 句：时间点缺失、零时长、逆序或超出歌曲范围，请手动检查。"); continue; }
            var units = line.Units.Zip(result.Units, (u,t) => u with { Timing = new(ProjectTime.FromSeconds(t.Start), ProjectTime.FromSeconds(t.End)), RawInputTime = null, IsAutoTimed = true, IsConfirmed = false }).ToArray();
            lines[index] = line with { Units = units, Timing = new(units[0].Timing.Start, units[^1].Timing.End), ExplicitEndTime = null };
            applied++;
        }
        // AI does not invent explicit Enter markers. Reject new overlapping rows rather than trimming sung tails.
        var changed = lines.Select((l,i) => l != source.Lines[i]).ToArray();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!changed[i]) continue;
            bool conflict = Enumerable.Range(0, lines.Length).Any(j => j != i && !lines[j].IsInstrumental && (changed[j] || Protected(lines[j]) || lines[j].Units.Any(u => u.IsAutoTimed))
                && (j < i ? lines[j].Timing.End > lines[i].Timing.Start : lines[i].Timing.End > lines[j].Timing.Start));
            if (conflict) { lines[i] = source.Lines[i]; changed[i] = false; applied--; skipped.Add($"第 {i+1} 句：与其他已对齐句重叠，保留原时间，请人工检查。"); }
        }
        return new(source with { Lines = lines }, applied, skipped);
    }
}
