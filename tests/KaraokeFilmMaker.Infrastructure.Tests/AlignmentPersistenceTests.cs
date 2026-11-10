using KaraokeFilmMaker.Application.Alignment;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Infrastructure.Alignment;

namespace KaraokeFilmMaker.Infrastructure.Tests;
public sealed class AlignmentPersistenceTests
{
    [Fact] public async Task AutomaticTimesRoundTripAndResetWithoutPretendingToBeManual()
    {
        string root=Path.Combine(Path.GetTempPath(),"alignment-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var range=new TimeRange(ProjectTime.FromSeconds(2),ProjectTime.FromSeconds(3));
            var unit=new LyricUnit(Guid.NewGuid(),[new(0,1)],"あ",null,range,null,false,"ja"){IsAutoTimed=true};
            var line=new LyricLine(Guid.NewGuid(),"あ",AnnotationType.None,null,null,range,[unit],ReviewState.Unreviewed,ReadingSource.Generated);
            var project=KaraokeProject.Create("auto",DateTimeOffset.UtcNow) with{Lyrics=new(LyricLanguage.Japanese,[line])};
            var repository=new JsonProjectRepository();await repository.SaveAsync(root,project);var saved=await repository.LoadAsync(root);
            var actual=Assert.Single(saved.Lyrics.Lines[0].Units);Assert.True(actual.IsAutoTimed);Assert.False(actual.IsConfirmed);Assert.True(LyricRecordingProgress.IsCompleted(actual));
            Assert.False(AlignmentEdits.Protected(saved.Lyrics.Lines[0]));
            var reset=LyricTimingService.ClearAllTimings(saved.Lyrics);Assert.False(reset.Lines[0].Units[0].IsAutoTimed);Assert.False(LyricRecordingProgress.IsCompleted(reset.Lines[0].Units[0]));
            Assert.Equal(unit.Id,reset.Lines[0].Units[0].Id);Assert.Equal("あ",reset.Lines[0].Units[0].Reading);
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact]public async Task CancelBeforeInstallCannotActivateAndGuideIsPackaged()
    {
        string root=Path.Combine(Path.GetTempPath(),"alignment-cancel-"+Guid.NewGuid().ToString("N"));
        var service=new LocalAlignmentService(root);using var cts=new CancellationTokenSource();cts.Cancel();
        try
        {
            Assert.Contains("install_alignment_plugin",LocalAlignmentService.InstallGuide);
            // Cancellation is checked before any download or hardware-specific side effects.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.InstallAsync("nextfire","cpu",null,cts.Token));
            Assert.False(File.Exists(Path.Combine(root,"nextfire.active")));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
