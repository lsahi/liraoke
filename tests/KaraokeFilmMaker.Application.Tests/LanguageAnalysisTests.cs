using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class LanguageAnalysisTests
{
    private static LanguageAnalysisService Service() => new(LanguageRegistry.CreateBuiltIns(new KanaReader()));
    private static LyricLine Line(string text) => new LyricImportService().Parse(text, ".txt", LyricLanguage.Auto).Lines[0]
        with { Timing = new(ProjectTime.Zero, ProjectTime.FromSeconds(10)), Units = [] };

    [Fact]
    public void IsolatedHanAndUnrecognizedLatinStayUnknown()
    {
        var resolver = new MixedLanguageResolver(LanguageRegistry.CreateBuiltIns(new KanaReader()));
        Assert.Equal(LanguageIds.Unknown, Assert.Single(resolver.Resolve("世界")).LanguageId);
        Assert.Equal(LanguageIds.Unknown, Assert.Single(resolver.Resolve("bonjour")).LanguageId);
        Assert.Equal(LanguageIds.Unknown, Assert.Single(resolver.Resolve("Привет")).LanguageId);
    }

    [Fact]
    public async Task MixedJapaneseAndEnglishUseDifferentPolicies()
    {
        var line = await Service().AnalyzeLineAsync(Line("大好き I love you"));
        Assert.Equal([LanguageIds.Japanese, LanguageIds.English], line.LanguageSpans!.Select(span => span.LanguageId));
        Assert.Equal(["I", "love", "you"], line.Units.Where(unit => unit.LanguageId == LanguageIds.English).Select(unit => unit.Reading));
        Assert.NotNull(line.Presentation);
        Assert.Equal(line.Units.Count, line.Presentation.RecordingCells.Count);
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Japanese, [line])));
    }

    [Fact]
    public async Task ChinesePhraseInsideJapaneseRunUsesPolicyBoundaries()
    {
        var line = await Service().AnalyzeLineAsync(Line("君と一起 sing a song"));
        Assert.Contains(line.LanguageSpans!, span => span.LanguageId == LanguageIds.Chinese
            && line.PrimaryText[span.Range.Start..span.Range.EndExclusive] == "一起");
        Assert.Contains(line.LanguageSpans!, span => span.LanguageId == LanguageIds.Japanese);
        Assert.Contains(line.LanguageSpans!, span => span.LanguageId == LanguageIds.English);
    }

    [Fact]
    public async Task ManualRangePreservesOtherUnitsAndLocksLanguage()
    {
        var service = Service();
        var line = await service.AnalyzeLineAsync(Line("hello 世界"));
        var original = line.Units[0] with { IsConfirmed = true, RawInputTime = ProjectTime.Zero };
        line = line with { Units = [original, .. line.Units.Skip(1)] };
        var result = await service.SetLanguageAsync(line, new(6, 2), LanguageIds.Japanese, "せかい");
        Assert.Equal(original, result.Units[0]);
        var span = result.LanguageSpans!.Single(span => span.Range.Start == 6);
        Assert.True(span.IsLocked);
        Assert.Equal("せかい", span.ManualReading);
        Assert.Equal(["せ", "か", "い"], result.Units.Skip(1).Select(unit => unit.Reading));
        Assert.Same(result, await service.AnalyzeLineAsync(result));
    }

    [Fact]
    public async Task WholeLineAnalysisProtectsLegacyConfirmedTiming()
    {
        var line = Line("hello") with { Units = [new(Guid.NewGuid(), [new(0, 5)], "hello", null,
            new(ProjectTime.Zero, ProjectTime.FromSeconds(10)), ProjectTime.Zero, true)] };
        Assert.Same(line, await Service().AnalyzeLineAsync(line));
    }

    [Fact]
    public void MigratedJapaneseSpansStillRequireKanaWithLegacyNullUnitLanguage()
    {
        var line = new LyricImportService().Parse("世界", ".txt", LyricLanguage.Chinese).Lines[0] with
        { LanguageSpans = [new(Guid.NewGuid(), new(0, 2), LanguageIds.Japanese, LanguageDetectionSource.DefaultHint, IsLocked: true)] };
        Assert.All(line.Units, unit => Assert.Null(unit.LanguageId));
        Assert.NotNull(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Japanese, [line])));
    }

    [Fact]
    public void MigratedJapaneseSpanRequiresEveryKanjiMappingNotJustOne()
    {
        var line = Line("世界") with
        {
            LanguageSpans = [new(Guid.NewGuid(), new(0, 2), LanguageIds.Japanese, LanguageDetectionSource.DefaultHint, IsLocked: true)],
            Units = [new(Guid.NewGuid(), [new(0, 1)], "せ", null, new(ProjectTime.Zero, ProjectTime.FromSeconds(10)), null, false)],
        };
        Assert.NotNull(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Japanese, [line])));
    }

    [Fact]
    public void MixedCoverageCheckRequiresJapaneseKanjiOnlyIncludingSurrogates()
    {
        var line = Line("𠮷中国") with
        {
            LanguageSpans = [new(Guid.NewGuid(), new(0, 2), LanguageIds.Japanese, LanguageDetectionSource.Manual),
                new(Guid.NewGuid(), new(2, 2), LanguageIds.Chinese, LanguageDetectionSource.Manual)],
            Units = [new(Guid.NewGuid(), [new(0, 2)], "よし", null, new(ProjectTime.Zero, ProjectTime.FromSeconds(10)), null, false)],
        };
        // Chinese text does not need Japanese ruby, even if the legacy document hint is Japanese.
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Japanese, [line])));
        Assert.NotNull(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Auto, [line with { Units = [] }])));
    }

    [Fact]
    public async Task ContextIsNotClaimedAsCertainAndDoesNotIgnoreSongMajority()
    {
        var document = new LyricImportService().Parse("我们一起\n我的梦\nこんにちは\n世界", ".txt", LyricLanguage.Auto);
        var result = await Service().AnalyzeDocumentAsync(document);
        var span = Assert.Single(result.Lines[^1].LanguageSpans!);
        Assert.Equal(LanguageIds.Chinese, span.LanguageId);
        Assert.Equal(LanguageDetectionSource.Context, span.Source);
        Assert.True(span.NeedsReview);
        Assert.True(LanguageAnalysisService.IsRecordingAdvisory(result.Lines[^1], span));
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(result));
    }

    [Fact]
    public async Task HanHintIsRecordedAsHintNotAutomaticDetection()
    {
        var line = await Service().AnalyzeLineAsync(Line("世界"), LanguageIds.Chinese);
        var span = Assert.Single(line.LanguageSpans!);
        Assert.Equal(LanguageDetectionSource.DefaultHint, span.Source);
        Assert.False(span.NeedsReview);
    }

    [Fact]
    public async Task SubtitleRubyKeepsWrittenBoundariesIndependentFromSingingUnits()
    {
        var line = await Service().SetLanguageAsync(Line("走ってった"), new(0, 5), LanguageIds.Japanese, "はしってった");
        Assert.Contains(line.Units, unit => unit.Reading == "しっ");
        var run = Assert.Single(line.Presentation!.SubtitleRuns, run => run.Range.Start == 0);
        Assert.Equal("はし", run.Annotation);
        Assert.Equal(new TextRange(0, 1), run.Range);
        Assert.NotNull(line.Presentation.RomanizedSubtitleRuns);
    }

    [Fact]
    public async Task UnknownJapaneseReadingBlocksRecordingUntilFixed()
    {
        var service = Service();
        var line = await service.SetLanguageAsync(Line("𠮷"), new(0, 2), LanguageIds.Japanese);
        Assert.NotNull(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Auto, [line])));
        line = await service.SetLanguageAsync(line, new(0, 2), LanguageIds.Japanese, "よし");
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Auto, [line])));
    }

    [Fact]
    public async Task QuickReadingCorrectionClearsOnlyReadingReview()
    {
        var service = Service();
        var line = await service.SetLanguageAsync(Line("吉"), new(0, 1), LanguageIds.Japanese);
        line = LyricUnitEditor.Edit(line, line.Units[0].Id, "吉", "き", LyricLanguage.Japanese);
        line = service.RefreshPresentation(line);
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Auto, [line])));
    }

    [Fact]
    public async Task SelectionMustNotSplitGraphemeOrSharedUnit()
    {
        var service = Service();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetLanguageAsync(Line("e\u0301"), new(0, 1), LanguageIds.English));
        var japanese = await service.SetLanguageAsync(Line("一緒"), new(0, 2), LanguageIds.Japanese, "いっしょ");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetLanguageAsync(japanese, new(1, 1), LanguageIds.Chinese));
    }

    [Fact]
    public async Task UnsupportedManualReadingIsRejectedRatherThanSilentlyIgnored()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().SetLanguageAsync(Line("hello"), new(0, 5), LanguageIds.English, "he-lo"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().SetLanguageAsync(Line("我"), new(0, 1), LanguageIds.Chinese, "wo"));
    }

    [Fact]
    public async Task QuickCorrectionPreservesLanguageSpanOffsetsAndIdentity()
    {
        var service = Service();
        var line = await service.AnalyzeLineAsync(Line("hello 一起"));
        var id = line.Units[0].Id;
        var edited = LyricUnitEditor.Edit(line, id, "hi", "hi", LyricLanguage.Auto);
        edited = service.RefreshPresentation(edited);
        Assert.Equal(id, edited.Units[0].Id);
        Assert.Equal(3, edited.LanguageSpans!.Single(span => span.LanguageId == LanguageIds.Chinese).Range.Start);
        Assert.Equal(edited.PrimaryText, edited.Presentation!.SourceText);
    }

    [Fact]
    public async Task SplitAndMergeKeepLanguageMetadataAndSnapshots()
    {
        var line = await Service().AnalyzeLineAsync(Line("hello 一起"));
        var (left, right) = LyricLineEditor.SplitBeforeUnit(line, 1);
        Assert.Equal(LanguageIds.English, Assert.Single(left.LanguageSpans!).LanguageId);
        Assert.Equal(LanguageIds.Chinese, Assert.Single(right.LanguageSpans!).LanguageId);
        Assert.Equal(right.PrimaryText, right.Presentation!.SourceText);
        var merged = LyricLineEditor.Merge(left, right);
        Assert.Equal(line.Units.Select(unit => unit.Id), merged.Units.Select(unit => unit.Id));
        Assert.Equal(merged.PrimaryText, merged.Presentation!.SourceText);
    }

    [Fact]
    public async Task MissingPluginKeepsSnapshotWithoutReanalysis()
    {
        var original = await Service().AnalyzeLineAsync(Line("hello"));
        var missing = new LanguageAnalysisService(new LanguageRegistry([]));
        Assert.Same(original, await missing.AnalyzeLineAsync(original));
        var refreshed = missing.RefreshPresentation(original);
        Assert.Equal(original.Presentation!.RecordingCells, refreshed.Presentation!.RecordingCells);
        Assert.Equal(original.Presentation.SubtitleRuns, refreshed.Presentation.SubtitleRuns);
    }

    [Theory]
    [InlineData(AnnotationType.Pinyin)]
    [InlineData(AnnotationType.Translation)]
    public async Task AnalysisKeepsExplicitAuxiliaryText(AnnotationType type)
    {
        var line = Line("我们一起") with { AnnotationType = type, AnnotationText = "my own annotation" };
        var result = await Service().AnalyzeLineAsync(line);
        Assert.Equal(type, result.AnnotationType);
        Assert.Equal(line.AnnotationText, result.AnnotationText);
    }

    [Fact]
    public async Task RecordingValidationUsesCachedPluginIssueWithoutInvokingPlugin()
    {
        var plugin = new CustomAdaptor();
        var service = new LanguageAnalysisService(new([plugin]));
        var line = await service.AnalyzeLineAsync(Line("hola"));
        line = line with { LanguageSpans = line.LanguageSpans!.Select(span => span with
            { DetectionNeedsReview = true, Source = LanguageDetectionSource.Context }).ToArray() };
        plugin.Rules.ThrowIfCalled = true;
        Assert.True(Assert.Single(line.LanguageSpans!).HasBlockingIssue);
        Assert.Contains("custom issue", LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Auto, [line])));
        Assert.Equal("CUSTOM", Assert.Single(line.Presentation!.RecordingCells).PrimaryText);
        Assert.All(line.Units, unit => { Assert.False(unit.IsConfirmed); Assert.Null(unit.RawInputTime); });
    }

    [Fact]
    public async Task PluginAnalysisFailureRemainsBlockingAcrossPresentationRefresh()
    {
        var plugin = new CustomAdaptor();
        plugin.Rules.ValidationIssue = null;
        plugin.Rules.AnalysisNeedsReview = true;
        var service = new LanguageAnalysisService(new([plugin]));
        var line = await service.AnalyzeLineAsync(Line("hola"));
        line = line with { LanguageSpans = line.LanguageSpans!.Select(span => span with
            { DetectionNeedsReview = true, Source = LanguageDetectionSource.Context }).ToArray() };
        line = service.RefreshPresentation(line);

        Assert.True(Assert.Single(line.LanguageSpans!).HasBlockingIssue);
        Assert.False(LanguageAnalysisService.IsRecordingAdvisory(line, Assert.Single(line.LanguageSpans!)));
        Assert.Contains("analysis issue", LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Auto, [line])));

        plugin.Rules.AnalysisNeedsReview = false;
        line = await service.AnalyzeLineAsync(line, force: true);
        Assert.False(Assert.Single(line.LanguageSpans!).HasBlockingIssue);
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Auto, [line])));
    }

    [Fact]
    public async Task ExplicitPluginFailureCannotHideBehindAConfidenceDiagnostic()
    {
        var plugin = new CustomAdaptor();
        plugin.Rules.ValidationIssue = "依据上下文或主要语言处理，请确认片段语言。";
        var line = await new LanguageAnalysisService(new([plugin])).AnalyzeLineAsync(Line("hola"));
        line = line with { LanguageSpans = line.LanguageSpans!.Select(span => span with
            { DetectionNeedsReview = true, Source = LanguageDetectionSource.Context }).ToArray() };
        Assert.True(Assert.Single(line.LanguageSpans!).HasBlockingIssue);
        Assert.False(LanguageAnalysisService.IsRecordingAdvisory(line, Assert.Single(line.LanguageSpans!)));
        Assert.NotNull(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Auto, [line])));
    }

    [Fact]
    public async Task MissingNamedPluginIsNotTheSameAsUnknownLanguage()
    {
        var source = Line("hola") with { LanguageSpans = [new(Guid.NewGuid(), new(0, 4), "missing",
            LanguageDetectionSource.Manual, IsLocked: true)] };
        var line = await Service().AnalyzeLineAsync(source);
        Assert.True(Assert.Single(line.LanguageSpans!).HasBlockingIssue);
        Assert.Contains("插件未安装", LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Auto, [line])));
    }

    [Fact]
    public async Task MalformedPluginCannotSplitCombiningCharacter()
    {
        var plugin = new CustomAdaptor { BrokenRange = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LanguageAnalysisService(new([plugin])).AnalyzeLineAsync(Line("e\u0301")));
    }

    [Fact]
    public async Task ReanalysisCanSplitOldUnitWithoutCreatingOverlappingTimingWindows()
    {
        var plugin = new CustomAdaptor(); plugin.Rules.BreakAfterFour = true;
        var source = Line("holahello");
        source = source with { Units = [new(Guid.NewGuid(), [new(0, 9)], "holahello", null, source.Timing, null, false)] };
        var result = await new LanguageAnalysisService(new([plugin])).AnalyzeLineAsync(source);
        Assert.Equal(2, result.Units.Count);
        Assert.Equal(result.Units[0].Timing.End, result.Units[1].Timing.Start);
    }

    [Fact]
    public void RightToLeftPluginIsExplicitlyRejected()
    {
        Assert.Throws<NotSupportedException>(() => new LanguageRegistry([new CustomAdaptor { Direction = LyricTextDirection.RightToLeft }]));
    }

    private sealed class KanaReader : IJapaneseReadingService
    {
        public Task<IReadOnlyList<JapaneseToken>> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
        {
            string reading = text.Replace("大好き", "だいすき", StringComparison.Ordinal).Replace("君", "きみ", StringComparison.Ordinal);
            return Task.FromResult<IReadOnlyList<JapaneseToken>>([new(text, reading, "", 0, text.Length, false)]);
        }
    }

    private sealed class CustomAdaptor : BaseLanguageAdaptor
    {
        public bool BrokenRange { get; init; }
        public LyricTextDirection Direction { get; init; }
        public CustomPolicy Rules { get; } = new();
        public override string LanguageId => "test";
        public override string DisplayName => "Test";
        public override LanguageCapabilities Capabilities => new(TextDirection: Direction);
        public override BaseLanguagePolicy Policy { get { Rules.BrokenRange = BrokenRange; return Rules; } }
    }
    private sealed class CustomPolicy : BaseLanguagePolicy
    {
        public bool ThrowIfCalled { get; set; }
        public bool BrokenRange { get; set; }
        public bool BreakAfterFour { get; set; }
        public bool AnalysisNeedsReview { get; set; }
        public string? ValidationIssue { get; set; } = "custom issue";
        public override IReadOnlyList<int> ProposeBoundaries(LanguageDetectionContext context) =>
            BreakAfterFour && context.Text.Length > 4 ? [4] : [];
        public override LanguageEvidence Detect(LanguageDetectionContext context)
        { if (ThrowIfCalled) { throw new InvalidOperationException(); } return new(100, "test"); }
        public override string? GetRecordingIssue(LyricLine line, LyricLanguageSpan span)
        { if (ThrowIfCalled) { throw new InvalidOperationException(); } return ValidationIssue; }
        public override Task<LanguageAnalysisResult> AnalyzeAsync(LanguageAnalysisContext context, CancellationToken cancellationToken = default)
        {
            var unit = new LyricUnit(Guid.NewGuid(), [new(0, BrokenRange ? 1 : context.Text.Length)], context.Text, null, context.Timing, ProjectTime.Zero, true);
            return Task.FromResult(new LanguageAnalysisResult([unit], [new(unit.Id, "CUSTOM")],
                [new(new(0, context.Text.Length), "ruby", null, [unit.Id])],
                NeedsReview: AnalysisNeedsReview, Diagnostic: AnalysisNeedsReview ? "analysis issue" : null));
        }
    }
}
