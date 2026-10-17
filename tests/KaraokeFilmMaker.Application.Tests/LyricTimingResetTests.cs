using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class LyricTimingResetTests
{
    [Theory]
    [InlineData(LyricLanguage.Chinese)]
    [InlineData(LyricLanguage.English)]
    [InlineData(LyricLanguage.Japanese)]
    public void ResetClearsOnlyTimingsAndConfirmationKeepingAllEditedContent(LyricLanguage language)
    {
        string text = language == LyricLanguage.Japanese ? "歌かな" : language == LyricLanguage.English ? "Sing again!" : "再次唱歌";
        var parsed = new LyricImportService().Parse(text + "\n" + text, ".txt", language);
        var source = new LyricSource("local", "song", "artist", null, "lyrics/source.txt", DateTimeOffset.UtcNow);
        var document = parsed with
        {
            Source = source,
            Lines = parsed.Lines.Select((line, index) =>
            {
                line = LyricTimingService.Retiming(line, new(ProjectTime.FromSeconds(2 + index * 4), ProjectTime.FromSeconds(4 + index * 4)));
                line = line with
                {
                    ReadingText = "manual reading", RomanizationText = "manual romaji", TranslationText = "manual translation",
                    ReadingSource = ReadingSource.Confirmed, ReviewState = ReviewState.Confirmed,
                    IsInstrumental = index == 1, IsHighlightEnabled = index == 0,
                    Units = line.Units.Select(unit => unit with { IsConfirmed = true, RawInputTime = unit.Timing.Start }).ToArray(),
                    LanguageSpans = [new(Guid.NewGuid(), new(0, text.Length), LanguageIds.FromLegacy(language)!,
                        LanguageDetectionSource.Manual, IsLocked: true, ManualReading: "user reading")],
                };
                return line with { Presentation = LanguagePresentation.BuildPlain(line) };
            }).ToArray(),
        };
        var reset = LyricTimingService.ClearAllTimings(document);
        Assert.Same(source, reset.Source);
        Assert.Equal(document.Language, reset.Language);
        for (int index = 0; index < reset.Lines.Count; index++)
        {
            var before = document.Lines[index]; var after = reset.Lines[index];
            Assert.Equal(new TimeRange(ProjectTime.Zero, ProjectTime.Zero), after.Timing);
            Assert.Same(before.Presentation, after.Presentation);
            Assert.Equal(before, after with { Timing = before.Timing, Units = before.Units });
            for (int unitIndex = 0; unitIndex < after.Units.Count; unitIndex++)
            {
                var oldUnit = before.Units[unitIndex]; var unit = after.Units[unitIndex];
                Assert.Equal(new TimeRange(ProjectTime.Zero, ProjectTime.Zero), unit.Timing);
                Assert.Null(unit.RawInputTime); Assert.False(unit.IsConfirmed);
                Assert.Equal(oldUnit, unit with { Timing = oldUnit.Timing, RawInputTime = oldUnit.RawInputTime, IsConfirmed = true });
            }
        }
        Assert.Equal(0, LyricRecordingProgress.From(reset).ConfirmedUnits);
        Assert.All(document.Lines.SelectMany(line => line.Units), unit => Assert.True(unit.IsConfirmed));
    }
}
