using KaraokeFilmMaker.Application.Separation;

namespace KaraokeFilmMaker.Application.Tests;

public class AudioExportVersionTests
{
    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 2)]
    public void VersionsHaveDistinctPredictableSuffixes(bool original, bool accompaniment, int count)
    {
        var versions = AudioExportVersions.Create(Path.Combine(Path.GetTempPath(), "思い出.mp4"), original, accompaniment);
        Assert.Equal(count, versions.Count);
        foreach (var version in versions)
            Assert.Equal("思い出 " + (version.Accompaniment ? "-off vocal-" : "-on vocal-") + ".mp4", Path.GetFileName(version.Path));
        Assert.Equal(count, versions.Select(v => v.Path).Distinct().Count());
    }

    [Theory]
    [InlineData("Song -on vocal-.mp4")]
    [InlineData("Song -off vocal-.mp4")]
    public void ReexportDoesNotAccumulateSuffixes(string input) =>
        Assert.Equal("Song -off vocal-.mp4", Path.GetFileName(AudioExportVersions.Create(input, false, true)[0].Path));

    [Fact]
    public void EmptySelectionIsRejected() => Assert.Throws<ArgumentException>(() => AudioExportVersions.Create("song.mp4", false, false));
}
