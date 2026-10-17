using KaraokeFilmMaker.Domain.Projects;
using System.Globalization;

namespace KaraokeFilmMaker.Domain.Validation;

public static class ProjectValidator
{
    public static IReadOnlyList<string> Validate(KaraokeProject project)
    {
        List<string> errors = [];

        if (!Enum.IsDefined(project.AudioVisualizationStyle))
        { errors.Add("Unknown audio visualization style."); }
        if (project.SchemaVersion <= 0 || project.SchemaVersion > KaraokeProject.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported schema version: {project.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(project.Title))
        {
            errors.Add("Project title is required.");
        }

        HashSet<Guid> assetIds = project.MediaAssets.Select(asset => asset.Id).ToHashSet();
        if (project.WorkArea is { } area)
        {
            var master = project.MediaAssets.FirstOrDefault(asset => asset.Id == project.MasterAudioAssetId);
            if (master is null || area.Duration.Microseconds <= 0 || area.End.Microseconds > master.Duration.Microseconds)
            { errors.Add("工作区裁切范围必须位于歌曲内，且结束时间必须大于开始时间。"); }
        }
        if (project.Metronome is { } metronome)
        {
            try { metronome.Validate(); }
            catch (ArgumentException exception) { errors.Add(exception.Message); }
        }
        if (project.Lyrics.Source is { } lyricSource)
        {
            string path = lyricSource.ImportedFileRelativePath.Replace('\\', '/');
            if (!path.StartsWith("lyrics/", StringComparison.Ordinal) || path.Contains(':')
                || path.Split('/').Any(segment => segment is ".." or "." or ""))
            { errors.Add("Imported lyric source must reference a portable file in lyrics/."); }
        }
        if (project.VideoTrackCount != 1)
        {
            errors.Add("当前版本只支持一条视频轨道。");
        }
        foreach (var asset in project.MediaAssets)
        {
            string normalized = asset.RelativePath.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/')
                || normalized.Contains(':') || normalized.Split('/').Any(segment => segment is ".." or "." or ""))
            {
                errors.Add($"Media asset {asset.Id} must have a portable path within the workspace.");
            }
        }

        if (project.MasterAudioAssetId is { } audioId && !assetIds.Contains(audioId))
        {
            errors.Add("Master audio references a missing media asset.");
        }
        else if (project.MasterAudioAssetId is { } masterId
            && project.MediaAssets.First(asset => asset.Id == masterId).Kind == Media.MediaKind.Image)
        {
            errors.Add("An image cannot be used as the master audio.");
        }

        foreach (var clip in project.VideoClips)
        {
            if (clip.TrackIndex < 0 || clip.TrackIndex >= project.VideoTrackCount)
            {
                errors.Add($"Video clip {clip.Id} references an invalid track.");
            }
            var asset = project.MediaAssets.FirstOrDefault(item => item.Id == clip.MediaAssetId);
            if (asset is null)
            {
                errors.Add($"Video clip {clip.Id} references a missing media asset.");
            }
            else if (clip.Duration.Microseconds <= 0
                || (asset.Kind == Media.MediaKind.Image
                    ? clip.SourceStart != Time.ProjectTime.Zero
                    : asset.Kind != Media.MediaKind.Video
                        || clip.SourceStart + clip.Duration > Time.ProjectTime.Zero + asset.Duration))
            {
                errors.Add($"Video clip {clip.Id} has an invalid source range.");
            }
        }

        foreach (var track in project.VideoClips.GroupBy(clip => clip.TrackIndex))
        {
            var orderedClips = track.OrderBy(clip => clip.TimelineStart).ToArray();
            for (var index = 1; index < orderedClips.Length; index++)
            {
                if (orderedClips[index].TimelineStart < orderedClips[index - 1].TimelineEnd)
                {
                    errors.Add("视频片段不能重叠，请调整位置或裁切片段。");
                    break;
                }
            }
        }

        foreach (var line in project.Lyrics.Lines)
        {
            if (line.Units.Select(unit => unit.Id).Distinct().Count() != line.Units.Count)
            { errors.Add($"Lyric line {line.Id} has duplicate unit identities."); }
            var textBoundaries = StringInfo.ParseCombiningCharacters(line.PrimaryText).Append(line.PrimaryText.Length).ToHashSet();
            if (line.LanguageSpans is { } spans)
            {
                int end = 0;
                foreach (var span in spans.OrderBy(span => span.Range.Start))
                {
                    if (string.IsNullOrWhiteSpace(span.LanguageId) || span.Range.Start < end
                        || span.Range.EndExclusive > line.PrimaryText.Length || !textBoundaries.Contains(span.Range.Start)
                        || !textBoundaries.Contains(span.Range.EndExclusive))
                    { errors.Add($"Lyric line {line.Id} has invalid language span ranges."); break; }
                    end = span.Range.EndExclusive;
                }
                if (spans.Select(span => span.Id).Distinct().Count() != spans.Count)
                { errors.Add($"Lyric line {line.Id} has duplicate language span identities."); }
            }
            if (line.Presentation is { } snapshot)
            {
                var unitIds = line.Units.Select(unit => unit.Id).ToHashSet();
                if (snapshot.SourceText != line.PrimaryText || snapshot.FormatVersion != 1
                    || snapshot.RecordingCells.Count != unitIds.Count
                    || snapshot.RecordingCells.Select(cell => cell.UnitId).Distinct().Count() != unitIds.Count
                    || snapshot.RecordingCells.Any(cell => !unitIds.Contains(cell.UnitId))
                    || HasOverlappingRuns(snapshot.SubtitleRuns) || HasOverlappingRuns(snapshot.RomanizedSubtitleRuns ?? [])
                    || snapshot.SubtitleRuns.Concat(snapshot.RomanizedSubtitleRuns ?? []).Any(run =>
                        run.Range.EndExclusive > line.PrimaryText.Length || !textBoundaries.Contains(run.Range.Start)
                        || !textBoundaries.Contains(run.Range.EndExclusive) || run.UnitIds.Any(id => !unitIds.Contains(id))))
                { errors.Add($"Lyric line {line.Id} has an invalid language presentation snapshot."); }
            }
            for (var index = 0; index < line.Units.Count; index++)
            {
                var unit = line.Units[index];
                if (unit.Timing.Start < line.Timing.Start || unit.Timing.End > line.Timing.End)
                {
                    errors.Add($"Lyric unit {unit.Id} falls outside its line timing.");
                }

                if (unit.DisplayRanges.Any(range => range.EndExclusive > line.PrimaryText.Length))
                {
                    errors.Add($"Lyric unit {unit.Id} references text outside its lyric line.");
                }

                if (index > 0 && unit.Timing.Start < line.Units[index - 1].Timing.End)
                {
                    errors.Add($"Lyric unit {unit.Id} overlaps the preceding unit.");
                }
            }
        }

        var style = project.SubtitleStyle;
        if (string.IsNullOrWhiteSpace(style.FontFamily)
            || style.FontFamily.Any(char.IsControl) || style.FontFamily.Contains("://", StringComparison.Ordinal)
            || !double.IsFinite(style.PrimaryFontSize) || style.PrimaryFontSize is <= 0 or > 512
            || !double.IsFinite(style.AnnotationFontSize) || style.AnnotationFontSize is <= 0 or > 512
            || !double.IsFinite(style.OutlineWidth) || style.OutlineWidth is < 0 or > 64
            || !double.IsFinite(style.ShadowOffset) || style.ShadowOffset is < 0 or > 64
            || !double.IsFinite(style.SafeAreaRatio) || style.SafeAreaRatio is <= 0 or > 1
            || !double.IsFinite(style.LineGap) || style.LineGap is < 0 or > 256
            || !double.IsFinite(style.MarginRatio) || style.MarginRatio is < 0 or >= 0.25
            || !IsHexColor(style.InactiveColor) || !IsHexColor(style.ActiveColor) || !IsHexColor(style.OutlineColor)
            || style.ActiveOutlineColor is not null && !IsHexColor(style.ActiveOutlineColor))
        {
            errors.Add("Subtitle style requires valid font sizes, colors, outline, shadow and safe area.");
        }

        if (project.ExportPreset.Width <= 0 || project.ExportPreset.Height <= 0
            || Math.Max(project.ExportPreset.Width, project.ExportPreset.Height) > 1920
            || Math.Min(project.ExportPreset.Width, project.ExportPreset.Height) > 1080)
        {
            errors.Add("MVP export resolution must be positive, with a long edge up to 1920 and a short edge up to 1080.");
        }

        if (project.ExportPreset.FramesPerSecond is not (24 or 30 or 60))
        {
            errors.Add("MVP export frame rate must be 24, 30, or 60 fps.");
        }

        return errors;
    }

    private static bool IsHexColor(string color) =>
        color is { Length: 7 or 9 } && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit);

    private static bool HasOverlappingRuns(IReadOnlyList<Lyrics.LyricSubtitleRun> runs)
    {
        int end = 0;
        foreach (var run in runs.OrderBy(run => run.Range.Start))
        { if (run.Range.Start < end) { return true; } end = run.Range.EndExclusive; }
        return false;
    }
}
