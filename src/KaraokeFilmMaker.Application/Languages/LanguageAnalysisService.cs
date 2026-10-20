using System.Globalization;
using KaraokeFilmMaker.Application.Japanese;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Languages;

public sealed class LanguageAnalysisService(LanguageRegistry registry)
{
    public LanguageRegistry Registry => registry;

    public async Task<LyricDocument> AnalyzeDocumentAsync(LyricDocument document, bool force = false,
        CancellationToken cancellationToken = default)
    {
        string context = string.Join("\n", document.Lines.Select(line => line.PrimaryText));
        List<LyricLine> lines = [];
        foreach (var line in document.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines.Add(await AnalyzeCoreAsync(line, LanguageIds.FromLegacy(document.Language), force, context, cancellationToken).ConfigureAwait(false));
        }
        return document with { Lines = lines };
    }

    public Task<LyricLine> AnalyzeLineAsync(LyricLine line, string? defaultHint = null, bool force = false,
        CancellationToken cancellationToken = default) => AnalyzeCoreAsync(line, defaultHint, force, line.PrimaryText, cancellationToken);

    private async Task<LyricLine> AnalyzeCoreAsync(LyricLine line, string? hint, bool force, string context, CancellationToken cancellationToken)
    {
        // Opening a project or normal refresh never rebuilds a confirmed/manual result.
        if (!force && (line.Presentation is not null || line.Units.Any(unit => unit.IsConfirmed || unit.RawInputTime is not null)
            || line.ReadingSource is ReadingSource.Manual or ReadingSource.Confirmed)) { return line; }
        if (line.PrimaryText.Length == 0) { return line; }
        var spans = new MixedLanguageResolver(registry).Resolve(line.PrimaryText, hint, context, line.LanguageSpans);
        return await RebuildAsync(line, spans, new TextRange(0, line.PrimaryText.Length), cancellationToken).ConfigureAwait(false);
    }

    public Task<LyricLine> SetLanguageAsync(LyricLine line, TextRange range, string languageId,
        string? manualReading = null, string? defaultHint = null, CancellationToken cancellationToken = default)
    {
        var adaptor = registry.Find(languageId) ?? throw new InvalidOperationException("未安装所选语言包。");
        if (!string.IsNullOrWhiteSpace(manualReading) && !adaptor.Capabilities.SupportsManualReading)
        { throw new InvalidOperationException("此语言包不支持片段读音重建，请使用单元精修修改读音。"); }
        ValidateRange(line, range);
        return ReplaceRangeAsync(line, range, [new(Guid.NewGuid(), range, adaptor.LanguageId,
            LanguageDetectionSource.Manual, IsLocked: true, ManualReading: string.IsNullOrWhiteSpace(manualReading) ? null : manualReading.Trim(),
            AdaptorVersion: adaptor.Version)], defaultHint, cancellationToken);
    }

    public Task<LyricLine> RedetectRangeAsync(LyricLine line, TextRange range, string? defaultHint = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(line, range);
        var spans = new MixedLanguageResolver(registry).Resolve(line.PrimaryText[range.Start..range.EndExclusive], defaultHint, line.PrimaryText)
            .Select(span => span with { Range = Shift(span.Range, range.Start) }).ToArray();
        return ReplaceRangeAsync(line, range, spans, defaultHint, cancellationToken);
    }

    private async Task<LyricLine> ReplaceRangeAsync(LyricLine line, TextRange range, IReadOnlyList<LyricLanguageSpan> replacement,
        string? hint, CancellationToken cancellationToken)
    {
        var original = line.LanguageSpans ?? new MixedLanguageResolver(registry).Resolve(line.PrimaryText, hint);
        List<LyricLanguageSpan> spans = [];
        foreach (var span in original)
        {
            if (!Intersects(span.Range, range)) { spans.Add(span); continue; }
            if (span.Range.Start < range.Start)
            { spans.Add(span with { Range = new(span.Range.Start, range.Start - span.Range.Start), ManualReading = null }); }
            if (span.Range.EndExclusive > range.EndExclusive)
            { spans.Add(span with { Id = Guid.NewGuid(), Range = new(range.EndExclusive, span.Range.EndExclusive - range.EndExclusive), ManualReading = null }); }
        }
        spans.AddRange(replacement);
        return await RebuildAsync(line, spans.OrderBy(span => span.Range.Start).ToArray(), range, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LyricLine> RebuildAsync(LyricLine line, IReadOnlyList<LyricLanguageSpan> spans, TextRange affected,
        CancellationToken cancellationToken)
    {
        var units = line.Units.Where(unit => !unit.DisplayRanges.Any(range => Intersects(range, affected))).ToList();
        var outputSpans = spans.ToArray();
        bool wholeLine = affected.Start == 0 && affected.EndExclusive == line.PrimaryText.Length;
        TimeRange affectedTiming = TimingFor(line, affected, preserveExisting: !wholeLine);
        Dictionary<Guid, LyricPresentationSnapshot> generated = [];
        for (int index = 0; index < outputSpans.Length; index++)
        {
            var span = outputSpans[index];
            if (!Intersects(span.Range, affected)) { continue; }
            cancellationToken.ThrowIfCancellationRequested();
            var spanTiming = new TimeRange(
                new(affectedTiming.Start.Microseconds + affectedTiming.Duration.Microseconds * (span.Range.Start - affected.Start) / affected.Length),
                new(affectedTiming.Start.Microseconds + affectedTiming.Duration.Microseconds * (span.Range.EndExclusive - affected.Start) / affected.Length));
            var context = new LanguageAnalysisContext(line.PrimaryText[span.Range.Start..span.Range.EndExclusive],
                spanTiming, span.ManualReading);
            var adaptor = registry.Find(span.LanguageId);
            LanguageAnalysisResult result;
            if (adaptor is null)
            {
                result = LanguagePresentation.AnalyzePlain(context, LyricLanguage.Unknown, span.LanguageId) with
                { NeedsReview = true, Diagnostic = span.LanguageId == LanguageIds.Unknown ? "无法确定语言，请指定片段语言。" : "所需语言插件未安装。" };
            }
            else
            {
                try { result = await adaptor.Policy.AnalyzeAsync(context, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { throw new InvalidOperationException($"语言插件 {adaptor.DisplayName} 分析失败：{error.Message}", error); }
            }
            ValidateResult(context, result);
            if (adaptor is not null)
            {
                string? issue;
                try { issue = adaptor.Policy.GetRecordingIssue(LanguagePresentation.CreateLine(context) with { Units = result.Units,
                        ReadingText = result.ReadingText, RomanizationText = result.RomanizationText },
                    span with { Range = new(0, context.Text.Length) }); }
                catch (Exception error) { throw new InvalidOperationException($"语言插件 {adaptor.DisplayName} 校验失败：{error.Message}", error); }
                if (issue is not null) { result = result with { NeedsReview = true, Diagnostic = issue }; }
            }
            generated[span.Id] = new(context.Text, result.RecordingCells, result.SubtitleRuns,
                RomanizedSubtitleRuns: result.RomanizedSubtitleRuns);
            units.AddRange(result.Units.Select(unit => unit with
            { DisplayRanges = unit.DisplayRanges.Select(range => Shift(range, span.Range.Start)).ToArray(), LanguageId = span.LanguageId,
                IsConfirmed = false, RawInputTime = null }));
            outputSpans[index] = span with { NeedsReview = span.DetectionNeedsReview || span.LanguageId == LanguageIds.Unknown || result.NeedsReview,
                Diagnostic = result.Diagnostic ?? span.Diagnostic, AdaptorVersion = adaptor?.Version ?? span.AdaptorVersion,
                // Unknown language uses the host's plain units; uncertainty is not a policy failure.
                HasBlockingIssue = span.LanguageId != LanguageIds.Unknown && result.NeedsReview };
        }
        var rebuilt = line with { LanguageSpans = outputSpans, Units = KaraokeFilmMaker.Application.Lyrics.RoleAssignments.Transfer(line,
            units.OrderBy(unit => unit.Timing.Start).ThenBy(unit => unit.DisplayRanges.Min(range => range.Start)).ToArray()),
            ReadingSource = outputSpans.Any(span => span.ManualReading is not null) ? ReadingSource.Manual : ReadingSource.Generated,
            ReviewState = outputSpans.Any(span => span.NeedsReview) ? ReviewState.ReviewLater : ReviewState.Unreviewed };
        bool allJapanese = outputSpans.All(span => span.LanguageId == LanguageIds.Japanese);
        bool preserveAuxiliary = line.AnnotationType is AnnotationType.Pinyin or AnnotationType.Translation;
        rebuilt = rebuilt with { ReadingText = allJapanese ? string.Concat(rebuilt.Units.Select(unit => unit.Reading)) : line.ReadingText,
            AnnotationText = preserveAuxiliary ? line.AnnotationText : allJapanese ? string.Concat(rebuilt.Units.Select(unit => unit.Reading)) : null,
            AnnotationType = preserveAuxiliary ? line.AnnotationType : allJapanese ? AnnotationType.Kana : AnnotationType.None,
            RomanizationText = allJapanese ? string.Join(" ", rebuilt.Units.Select(unit => unit.Romanization)) : null };
        return RefreshPresentationCore(rebuilt, generated);
    }

    /// <summary>Explicit edit-time refresh only. Playback consumes the snapshot without invoking any plugin.</summary>
    public LyricLine RefreshPresentation(LyricLine line)
        => RefreshPresentationCore(line, null);

    private LyricLine RefreshPresentationCore(LyricLine line, IReadOnlyDictionary<Guid, LyricPresentationSnapshot>? generated)
    {
        if (line.LanguageSpans is null) { return line; }
        List<LyricRecordingCell> cells = [];
        List<LyricSubtitleRun> runs = [], romanized = [];
        List<LyricLanguageSpan> updatedSpans = [];
        foreach (var span in line.LanguageSpans)
        {
            var local = line with { PrimaryText = line.PrimaryText[span.Range.Start..span.Range.EndExclusive],
                Units = line.Units.Where(unit => unit.DisplayRanges.All(range => range.Start >= span.Range.Start && range.EndExclusive <= span.Range.EndExclusive))
                    .Select(unit => unit with { DisplayRanges = unit.DisplayRanges.Select(range => Shift(range, -span.Range.Start)).ToArray() }).ToArray(),
                LanguageSpans = null, Presentation = null };
            var adaptor = registry.Find(span.LanguageId);
            var updatedSpan = span;
            if (generated is null && adaptor is not null)
            {
                string? issue;
                try { issue = adaptor.Policy.GetRecordingIssue(local, span with { Range = new(0, local.PrimaryText.Length) }); }
                catch (Exception error) { throw new InvalidOperationException($"语言插件 {span.LanguageId} 校验失败：{error.Message}", error); }
                if (span.LanguageId == LanguageIds.Japanese && local.Units.Any(JapaneseLanguagePolicy.HasUnresolvedReading))
                { issue = "日语读音尚未完整，请校正假名。"; }
                // A display refresh does not rerun AnalyzeAsync. Retain its cached failures until
                // explicit reanalysis; Japanese reading corrections can be checked completely here.
                bool retainAnalysisIssue = span.LanguageId != LanguageIds.Japanese
                    && (span.HasBlockingIssue == true || (span.HasBlockingIssue is null && span.NeedsReview
                        && !HasLegacyDetectionAdvisory(span)));
                updatedSpan = span with { NeedsReview = span.DetectionNeedsReview || issue is not null || retainAnalysisIssue,
                    Diagnostic = issue ?? (retainAnalysisIssue ? span.Diagnostic
                        : span.DetectionNeedsReview ? "依据上下文处理，请确认片段语言。" : null),
                    HasBlockingIssue = issue is not null || retainAnalysisIssue };
            }
            updatedSpans.Add(updatedSpan);
            LyricPresentationSnapshot presentation;
            if (generated is not null && generated.TryGetValue(span.Id, out var freshlyGenerated))
            { presentation = freshlyGenerated; }
            else if (adaptor is null && line.Presentation is { } saved && saved.SourceText == line.PrimaryText)
            {
                cells.AddRange(saved.RecordingCells.Where(cell => local.Units.Any(unit => unit.Id == cell.UnitId)));
                runs.AddRange(saved.SubtitleRuns.Where(run => run.Range.Start >= span.Range.Start && run.Range.EndExclusive <= span.Range.EndExclusive));
                romanized.AddRange((saved.RomanizedSubtitleRuns ?? saved.SubtitleRuns).Where(run => run.Range.Start >= span.Range.Start && run.Range.EndExclusive <= span.Range.EndExclusive));
                continue;
            }
            else
            {
                try { presentation = adaptor?.Policy.CreatePresentation(local) ?? LanguagePresentation.BuildPlain(local); }
                catch (Exception error) { throw new InvalidOperationException($"语言插件 {span.LanguageId} 显示规则失败：{error.Message}", error); }
            }
            ValidatePresentation(local, presentation);
            cells.AddRange(presentation.RecordingCells);
            runs.AddRange(presentation.SubtitleRuns.Select(run => run with { Range = Shift(run.Range, span.Range.Start), LanguageId = span.LanguageId }));
            romanized.AddRange((presentation.RomanizedSubtitleRuns ?? presentation.SubtitleRuns).Select(run => run with { Range = Shift(run.Range, span.Range.Start), LanguageId = span.LanguageId }));
        }
        return line with { LanguageSpans = updatedSpans, Presentation = new(line.PrimaryText, cells, runs, RomanizedSubtitleRuns: romanized) };
    }

    public static string? GetRecordingIssue(LyricDocument document, int firstLine = 0)
    {
        for (int i = firstLine; i < document.Lines.Count; i++)
        {
            var line = document.Lines[i];
            if (line.IsInstrumental || !line.IsHighlightEnabled) { continue; }
            foreach (var span in line.LanguageSpans ?? [])
            {
                if (span.HasBlockingIssue == true)
                { return $"第 {i + 1} 句：{span.Diagnostic ?? "请确认语言和读音。"}"; }
                if ((span.NeedsReview || span.LanguageId == LanguageIds.Unknown) && !IsRecordingAdvisory(line, span))
                {
                    return !HasUsableRecordingUnits(line, span)
                        ? $"第 {i + 1} 句的演唱单元映射尚未完整，请先修正分词或读音。"
                        : $"第 {i + 1} 句：{span.Diagnostic ?? "请确认语言和读音。"}";
                }
            }
            if (line.Units.Any(unit => (unit.LanguageId ?? line.LanguageSpans?.FirstOrDefault(span =>
                unit.DisplayRanges.Any(range => Intersects(range, span.Range)))?.LanguageId) == LanguageIds.Japanese
                    && JapaneseLanguagePolicy.HasUnresolvedReading(unit)))
            { return $"第 {i + 1} 句尚未完成日语假名读音。"; }
            if (line.LanguageSpans?.Any(span => span.LanguageId == LanguageIds.Japanese
                && JapaneseLanguagePolicy.HasUncoveredKanji(line, span.Range)) == true)
            { return $"第 {i + 1} 句尚未完成日语汉字读音映射。"; }
        }
        return JapaneseAnnotationService.GetRecordingIssue(document, firstLine);
    }

    /// <summary>
    /// Detection confidence is advisory when existing units are usable. Never runs detection or a
    /// plugin, rebuilds units, or changes saved timing. Analysis/reading errors remain blocking.
    /// </summary>
    public static bool IsRecordingAdvisory(LyricLine line, LyricLanguageSpan span)
    {
        if ((!span.NeedsReview && span.LanguageId != LanguageIds.Unknown) || span.HasBlockingIssue == true
            || (span.HasBlockingIssue is null && !HasLegacyDetectionAdvisory(span))
            || !HasUsableRecordingUnits(line, span))
        { return false; }
        if (span.LanguageId == LanguageIds.Japanese && (JapaneseLanguagePolicy.HasUncoveredKanji(line, span.Range)
            || line.Units.Any(unit => unit.DisplayRanges.Any(range => Intersects(range, span.Range))
                && JapaneseLanguagePolicy.HasUnresolvedReading(unit))))
        { return false; }
        return true;
    }

    private static bool HasLegacyDetectionAdvisory(LyricLanguageSpan span) => span.DetectionNeedsReview
        && span.Source is LanguageDetectionSource.Context or LanguageDetectionSource.Unknown
        && span.Diagnostic is ("依据上下文或主要语言处理，请确认片段语言。" or "依据上下文处理，请确认片段语言。"
            or "无法确定语言，请指定片段语言。" or "语言证据不足，请指定语言片段。");

    private static bool HasUsableRecordingUnits(LyricLine line, LyricLanguageSpan span)
    {
        var boundaries = StringInfo.ParseCombiningCharacters(line.PrimaryText).Append(line.PrimaryText.Length).ToHashSet();
        if (span.Range.Length <= 0 || span.Range.EndExclusive > line.PrimaryText.Length
            || !boundaries.Contains(span.Range.Start) || !boundaries.Contains(span.Range.EndExclusive))
        { return false; }
        var units = line.Units.Where(unit => unit.DisplayRanges.Any(range => Intersects(range, span.Range)))
            .ToArray();
        if (units.Select(unit => unit.Id).Distinct().Count() != units.Length
            || units.Any(unit => string.IsNullOrWhiteSpace(unit.Reading)
                || (unit.LanguageId is not null && unit.LanguageId != span.LanguageId)
                || unit.DisplayRanges.Any(range => range.Length <= 0 || range.Start < span.Range.Start
                    || range.EndExclusive > span.Range.EndExclusive || !boundaries.Contains(range.Start)
                    || !boundaries.Contains(range.EndExclusive))))
        { return false; }
        var elements = StringInfo.GetTextElementEnumerator(line.PrimaryText[span.Range.Start..span.Range.EndExclusive]);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (!element.EnumerateRunes().Any(System.Text.Rune.IsLetterOrDigit)) { continue; }
            int start = span.Range.Start + elements.ElementIndex;
            if (!units.Any(unit => unit.DisplayRanges.Any(range => range.Start <= start && range.EndExclusive >= start + element.Length)))
            { return false; }
        }
        if (line.Presentation is { } presentation)
        {
            try { ValidatePresentation(line, presentation); }
            catch (InvalidOperationException) { return false; }
        }
        return true;
    }

    private static TimeRange TimingFor(LyricLine line, TextRange range, bool preserveExisting)
    {
        var old = line.Units.Where(unit => unit.DisplayRanges.Any(item => Intersects(item, range))).ToArray();
        if (preserveExisting && old.Length > 0) { return new(old.Min(unit => unit.Timing.Start), old.Max(unit => unit.Timing.End)); }
        return new(new(line.Timing.Start.Microseconds + line.Timing.Duration.Microseconds * range.Start / line.PrimaryText.Length),
            new(line.Timing.Start.Microseconds + line.Timing.Duration.Microseconds * range.EndExclusive / line.PrimaryText.Length));
    }
    private static void ValidateRange(LyricLine line, TextRange range)
    {
        var boundaries = StringInfo.ParseCombiningCharacters(line.PrimaryText).Append(line.PrimaryText.Length).ToHashSet();
        if (range.EndExclusive > line.PrimaryText.Length || !boundaries.Contains(range.Start) || !boundaries.Contains(range.EndExclusive))
        { throw new InvalidOperationException("语言片段必须选择完整字符。"); }
        if (line.Units.Any(unit => unit.DisplayRanges.Any(item => Intersects(item, range))
            && unit.DisplayRanges.Any(item => item.Start < range.Start || item.EndExclusive > range.EndExclusive)))
        { throw new InvalidOperationException("所选范围切开了一个共享文字的演唱单元，请选择完整词组。"); }
    }
    private static void ValidateResult(LanguageAnalysisContext context, LanguageAnalysisResult result)
    {
        var boundaries = StringInfo.ParseCombiningCharacters(context.Text).Append(context.Text.Length).ToHashSet();
        if (result.Units.Select(unit => unit.Id).Distinct().Count() != result.Units.Count
            || (result.Units.Count == 0 && context.Text.EnumerateRunes().Any(System.Text.Rune.IsLetterOrDigit) && !result.NeedsReview)
            || result.Units.Any(unit => unit.DisplayRanges.Count == 0 || unit.DisplayRanges.Any(range => range.EndExclusive > context.Text.Length)
                || unit.DisplayRanges.Any(range => !boundaries.Contains(range.Start) || !boundaries.Contains(range.EndExclusive))
                || unit.Timing.Start < context.Timing.Start || unit.Timing.End > context.Timing.End)
            || result.Units.Skip(1).Where((unit, index) => unit.Timing.Start < result.Units[index].Timing.End).Any())
        { throw new InvalidOperationException("语言插件返回了无效的单元映射或时间范围。"); }
    }
    private static void ValidatePresentation(LyricLine line, LyricPresentationSnapshot presentation)
    {
        var ids = line.Units.Select(unit => unit.Id).ToHashSet();
        var boundaries = StringInfo.ParseCombiningCharacters(line.PrimaryText).Append(line.PrimaryText.Length).ToHashSet();
        if (presentation.SourceText != line.PrimaryText || presentation.FormatVersion != 1
            || presentation.RecordingCells.Count != ids.Count || presentation.RecordingCells.Select(cell => cell.UnitId).Distinct().Count() != ids.Count
            || presentation.RecordingCells.Any(cell => !ids.Contains(cell.UnitId) || string.IsNullOrWhiteSpace(cell.PrimaryText))
            || HasOverlappingRuns(presentation.SubtitleRuns) || HasOverlappingRuns(presentation.RomanizedSubtitleRuns ?? [])
            || presentation.SubtitleRuns.Concat(presentation.RomanizedSubtitleRuns ?? []).Any(run =>
                run.Range.EndExclusive > line.PrimaryText.Length || !boundaries.Contains(run.Range.Start)
                || !boundaries.Contains(run.Range.EndExclusive) || run.UnitIds.Any(id => !ids.Contains(id))))
        { throw new InvalidOperationException("语言插件返回了无效的显示快照。"); }
    }
    private static bool HasOverlappingRuns(IReadOnlyList<LyricSubtitleRun> runs)
    {
        int end = 0;
        foreach (var run in runs.OrderBy(run => run.Range.Start))
        { if (run.Range.Start < end) { return true; } end = run.Range.EndExclusive; }
        return false;
    }
    private static TextRange Shift(TextRange range, int offset) => new(range.Start + offset, range.Length);
    private static bool Intersects(TextRange a, TextRange b) => a.Start < b.EndExclusive && b.Start < a.EndExclusive;
}
