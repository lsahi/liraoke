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
public sealed record AlignmentLineResult(Guid Id, IReadOnlyList<AlignmentUnitResult> Units, string? Error = null, string? Review = null);
public sealed record AlignmentOutcome(IReadOnlyList<AlignmentLineResult> Lines, string LogPath);
public sealed record AlignmentApplyResult(LyricDocument Document, int Applied, IReadOnlyList<string> Skipped);

public static class AlignmentEdits
{
    public static bool Protected(LyricLine line) => line.Units.Count > 0 && line.Units.All(u => u.IsConfirmed || u.RawInputTime is not null);
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
            var points = result.Units.ToArray();
            if(points.Length==line.Units.Count)
                for(int i=0;i<points.Length;i++)
                    if(line.Units[i].IsConfirmed || line.Units[i].RawInputTime is not null)
                        points[i]=new(line.Units[i].Id,line.Units[i].Timing.Start.TotalSeconds,line.Units[i].Timing.End.TotalSeconds);
            var resolved = result with { Units=points };
            bool valid = resolved.Units.Count > 0 && resolved.Units.Count == line.Units.Count;
            double previous = -1;
            for (int i = 0; valid && i < resolved.Units.Count; i++)
            {
                var u = resolved.Units[i]; valid = u.Id == line.Units[i].Id && double.IsFinite(u.Start) && double.IsFinite(u.End)
                    && u.Start >= 0 && u.Start >= previous && u.End > u.Start && u.End <= duration;
                previous = u.End;
            }
            if (!valid) { skipped.Add($"第 {index+1} 句：时间点缺失、零时长、逆序或超出歌曲范围，请手动检查。"); continue; }
            var units = line.Units.Zip(resolved.Units, (u,t) => u.IsConfirmed || u.RawInputTime is not null ? u : u with { Timing = new(ProjectTime.FromSeconds(t.Start), ProjectTime.FromSeconds(t.End)), RawInputTime = null, IsAutoTimed = true, IsConfirmed = false }).ToArray();
            for(int i=0;i+1<units.Length;i++)
                if(units[i].IsAutoTimed && units[i].Timing.End < units[i+1].Timing.Start)
                    units[i]=units[i] with {Timing=new(units[i].Timing.Start,units[i+1].Timing.Start)};
            lines[index] = line with { Units = units, Timing = new(units[0].Timing.Start, units[^1].Timing.End), ExplicitEndTime = null, AutoAlignmentEnd = units[^1].Timing.End, AlignmentReviewMessage = result.Review };
            applied++;
        }
        // Automatic terminal boundaries authorize adjacent overlap without impersonating Enter taps.
        var changed = lines.Select((l,i) => l != source.Lines[i]).ToArray();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!changed[i]) continue;
            bool conflict = false;
            for(int j=0;j<lines.Length;j++)
            {
                if(j==i || lines[j].IsInstrumental || !lines[j].Units.All(KaraokeFilmMaker.Application.Lyrics.LyricRecordingProgress.IsCompleted) || lines[j].Units.Count==0)continue;
                int first=Math.Min(i,j),last=Math.Max(i,j);
                if(lines[first].Timing.Start>=lines[last].Timing.Start ||
                    (lines[first].Timing.End>lines[last].Timing.Start && (last-first>1 || !KaraokeFilmMaker.Application.Lyrics.LyricTimingConflicts.HasExplicitEnd(lines[first])))) conflict=true;
            }
            if (conflict) { lines[i] = source.Lines[i]; changed[i] = false; applied--; skipped.Add($"第 {i+1} 句：与其他已对齐句重叠，保留原时间，请人工检查。"); }
        }
        return new(source with { Lines = lines }, applied, skipped);
    }
}
