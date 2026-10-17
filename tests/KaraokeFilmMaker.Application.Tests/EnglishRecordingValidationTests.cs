using System.Text.Json;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class EnglishRecordingValidationTests
{
    [Theory]
    [InlineData("Through silver clouds")]
    [InlineData("You've crossed star-lit rivers")]
    [InlineData("At 12.5, you've crossed star-lit rivers")]
    public async Task ContextOnlyEnglishReviewDoesNotBlockTheWholeSong(string text)
    {
        var document = await AnalyzeAsync(text);
        var line = document.Lines[^1];
        var span = Assert.Single(line.LanguageSpans!);

        Assert.Equal(LanguageIds.English, span.LanguageId);
        Assert.Equal(LanguageDetectionSource.Context, span.Source);
        Assert.True(span.NeedsReview);
        Assert.True(span.DetectionNeedsReview);
        Assert.False(span.HasBlockingIssue);
        Assert.True(LanguageAnalysisService.IsRecordingAdvisory(line, span));
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(document));
        // Still retain uncertainty for the language editor; no change in detection confidence.
        Assert.Equal(ReviewState.ReviewLater, line.ReviewState);
    }

    [Fact]
    public async Task PreviouslySavedEnglishUnitsCanRecordWithoutReanalysisOrMutation()
    {
        var analyzed = await AnalyzeAsync("Through silver clouds");
        var source = analyzed.Lines[^1];
        source = source with { Units = source.Units.Select((unit, index) => unit with
            { IsConfirmed = index == 0, RawInputTime = index == 0 ? unit.Timing.Start : null }).ToArray(),
            LanguageSpans = source.LanguageSpans!.Select(span => span with { HasBlockingIssue = null }).ToArray() };
        analyzed = analyzed with { Lines = [.. analyzed.Lines.SkipLast(1), source] };
        var restored = JsonSerializer.Deserialize<LyricDocument>(JsonSerializer.Serialize(analyzed))!;
        string before = JsonSerializer.Serialize(restored);
        var units = restored.Lines[^1].Units;
        var spans = restored.Lines[^1].LanguageSpans;
        var snapshot = restored.Lines[^1].Presentation;

        Assert.Null(LanguageAnalysisService.GetRecordingIssue(restored));

        Assert.Equal(before, JsonSerializer.Serialize(restored));
        Assert.Same(units, restored.Lines[^1].Units);
        Assert.Same(spans, restored.Lines[^1].LanguageSpans);
        Assert.Same(snapshot, restored.Lines[^1].Presentation);
        Assert.Equal(source.Units.Select(unit => unit.Id), units.Select(unit => unit.Id));
        Assert.Equal(source.Units.Select(unit => unit.Timing), units.Select(unit => unit.Timing));
        Assert.Equal(source.Units.Select(unit => unit.RawInputTime), units.Select(unit => unit.RawInputTime));
    }

    [Fact]
    public async Task RefreshedDetectionDiagnosticIsAlsoAdvisoryWithoutClearingReview()
    {
        var document = await AnalyzeAsync("Through silver clouds");
        var refreshed = Service().RefreshPresentation(document.Lines[^1]);
        var span = Assert.Single(refreshed.LanguageSpans!);

        Assert.Equal("依据上下文处理，请确认片段语言。", span.Diagnostic);
        Assert.True(LanguageAnalysisService.IsRecordingAdvisory(refreshed, span));
        Assert.True(span.NeedsReview);
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Auto, [refreshed])));
    }

    [Fact]
    public async Task MixedSentenceKeepsJapaneseReadingValidationIndependentOfEnglishAdvisory()
    {
        var document = await AnalyzeAsync("大好き Through silver clouds");
        var line = document.Lines[^1];
        var english = Assert.Single(line.LanguageSpans!, span => span.LanguageId == LanguageIds.English);
        Assert.True(LanguageAnalysisService.IsRecordingAdvisory(line, english));
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(document));

        var unresolved = line with { Units = line.Units.Select(unit => unit.LanguageId == LanguageIds.Japanese
            ? unit with { Reading = "大" } : unit).ToArray() };
        Assert.True(LanguageAnalysisService.IsRecordingAdvisory(unresolved, english));
        Assert.Contains("日语假名读音", LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Auto, [unresolved])));
    }

    [Theory]
    [InlineData("bonjour")]
    [InlineData("Привет")]
    [InlineData("世界")]
    public async Task IsolatedUnknownTextIsNotReinterpretedAsEnglish(string text)
    {
        var document = new LyricImportService().Parse(text, ".txt", LyricLanguage.Auto);
        document = await Service().AnalyzeDocumentAsync(document);
        var line = Assert.Single(document.Lines);
        var span = Assert.Single(line.LanguageSpans!);

        Assert.Equal(LanguageIds.Unknown, span.LanguageId);
        Assert.False(span.HasBlockingIssue);
        Assert.True(LanguageAnalysisService.IsRecordingAdvisory(line, span));
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(document));
        var legacySpan = span with { HasBlockingIssue = null };
        var legacyLine = line with { LanguageSpans = [legacySpan] };
        Assert.True(LanguageAnalysisService.IsRecordingAdvisory(legacyLine, legacySpan));
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Auto, [legacyLine])));
    }

    [Theory]
    [InlineData("stale snapshot")]
    [InlineData("missing cell")]
    [InlineData("wrong cell")]
    [InlineData("missing unit")]
    [InlineData("duplicate unit")]
    [InlineData("wrong unit language")]
    [InlineData("partial word")]
    [InlineData("empty reading")]
    public async Task IncompleteSavedWordMappingsStillBlockWithoutBeingRebuilt(string condition)
    {
        var document = await AnalyzeAsync("Through silver clouds");
        var line = document.Lines[^1];
        var span = Assert.Single(line.LanguageSpans!);
        var first = line.Units[0];
        line = condition switch
        {
            "stale snapshot" => line with { Presentation = line.Presentation! with { SourceText = "old lyrics" } },
            "missing cell" => line with { Presentation = line.Presentation! with { RecordingCells = line.Presentation.RecordingCells.Skip(1).ToArray() } },
            "wrong cell" => line with { Presentation = line.Presentation! with { RecordingCells = line.Presentation.RecordingCells.Select(cell => cell.UnitId == first.Id ? cell with { PrimaryText = "" } : cell).ToArray() } },
            "missing unit" => line with { Units = line.Units.Skip(1).ToArray() },
            "duplicate unit" => line with { Units = [first, first, .. line.Units.Skip(2)] },
            "wrong unit language" => line with { Units = [first with { LanguageId = "test" }, .. line.Units.Skip(1)] },
            "partial word" => line with { Units = [first with { DisplayRanges = [new(first.DisplayRanges[0].Start, 1)] }, .. line.Units.Skip(1)] },
            "empty reading" => line with { Units = [first with { Reading = "" }, .. line.Units.Skip(1)] },
            _ => throw new InvalidOperationException(),
        };
        string before = JsonSerializer.Serialize(line);

        Assert.False(LanguageAnalysisService.IsRecordingAdvisory(line, span));
        Assert.NotNull(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Auto, [line])));
        Assert.Equal(before, JsonSerializer.Serialize(line));
    }

    [Theory]
    [InlineData("plugin error")]
    [InlineData("missing diagnostic")]
    [InlineData("missing detection review")]
    [InlineData("manual source")]
    [InlineData("other language")]
    public async Task LegacyReviewFlagAloneDoesNotMakeErrorsOrInvalidMappingsAdvisory(string condition)
    {
        var document = await AnalyzeAsync("Through silver clouds");
        var line = document.Lines[^1];
        var span = Assert.Single(line.LanguageSpans!) with { HasBlockingIssue = null };
        span = condition switch
        {
            "plugin error" => span with { Diagnostic = "custom issue" },
            "missing diagnostic" => span with { Diagnostic = null },
            "missing detection review" => span with { DetectionNeedsReview = false },
            "manual source" => span with { Source = LanguageDetectionSource.Manual },
            "other language" => span with { LanguageId = "test" },
            _ => throw new InvalidOperationException(),
        };
        line = line with { LanguageSpans = [span] };

        Assert.False(LanguageAnalysisService.IsRecordingAdvisory(line, span));
        Assert.NotNull(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Auto, [line])));
    }

    [Fact]
    public async Task ExistingUnitsWithoutSnapshotCanUseTheHostFallback()
    {
        var document = await AnalyzeAsync("Through silver clouds");
        var line = document.Lines[^1] with { Presentation = null };
        var span = Assert.Single(line.LanguageSpans!);
        Assert.True(LanguageAnalysisService.IsRecordingAdvisory(line, span));
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Auto, [line])));
        Assert.Null(line.Presentation);
    }

    [Fact]
    public async Task UnknownLanguageWithoutUsableUnitsRemainsBlocking()
    {
        var document = await Service().AnalyzeDocumentAsync(new LyricImportService().Parse("bonjour", ".txt", LyricLanguage.Auto));
        var line = Assert.Single(document.Lines) with { Units = [], Presentation = null };
        Assert.False(LanguageAnalysisService.IsRecordingAdvisory(line, Assert.Single(line.LanguageSpans!)));
        Assert.Contains("演唱单元映射", LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Auto, [line])));
    }

    [Fact]
    public async Task ExplicitNonblockingConfidenceDoesNotDependOnLanguageOrDiagnosticWording()
    {
        var document = await AnalyzeAsync("Through silver clouds");
        var line = document.Lines[^1];
        var span = Assert.Single(line.LanguageSpans!) with { LanguageId = "test", AdaptorVersion = "3.0.0",
            Diagnostic = "Please review this language selection.", HasBlockingIssue = false };
        line = line with { LanguageSpans = [span], Units = line.Units.Select(unit => unit with { LanguageId = "test" }).ToArray() };
        Assert.True(LanguageAnalysisService.IsRecordingAdvisory(line, span));
        Assert.Null(LanguageAnalysisService.GetRecordingIssue(new(LyricLanguage.Auto, [line])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(null)]
    public async Task BlockingClassificationSurvivesSerializationWithoutChangingConstructor(bool? hasBlockingIssue)
    {
        var document = await AnalyzeAsync("Through silver clouds");
        var span = Assert.Single(document.Lines[^1].LanguageSpans!) with { HasBlockingIssue = hasBlockingIssue };
        string json = JsonSerializer.Serialize(span);
        var restored = JsonSerializer.Deserialize<LyricLanguageSpan>(json)!;
        Assert.Equal(span, restored);
        Assert.Equal(hasBlockingIssue, restored.HasBlockingIssue);
        if (hasBlockingIssue is null) { Assert.DoesNotContain("HasBlockingIssue", json); }
    }

    private static Task<LyricDocument> AnalyzeAsync(string finalLine) => Service().AnalyzeDocumentAsync(
        new LyricImportService().Parse("Come with us, come with us\nTo a place you've never seen\nI love you\n" + finalLine,
            ".txt", LyricLanguage.Auto));

    private static LanguageAnalysisService Service() => new(LanguageRegistry.CreateBuiltIns(new KanaReader()));

    private sealed class KanaReader : IJapaneseReadingService
    {
        public Task<IReadOnlyList<JapaneseToken>> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<JapaneseToken>>([new(text,
                text.Replace("大好き", "だいすき", StringComparison.Ordinal), "", 0, text.Length, false)]);
    }
}
