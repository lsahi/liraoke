using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Infrastructure.Workspaces;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class WorkspaceServiceTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "KaraokeFilmMaker.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateBuildsPortableWorkspaceAndPersistsProject()
    {
        var repository = new JsonProjectRepository();
        var service = new WorkspaceService(repository, new StubMetadataReader());

        WorkspaceSession created = await service.CreateAsync(_testRoot, "未来へ");
        WorkspaceSession reopened = await service.OpenAsync(_testRoot);

        Assert.Equal(created.Project.Id, reopened.Project.Id);
        Assert.Equal("未来へ", reopened.Project.Title);
        Assert.Equal(Path.Combine(_testRoot, "未来へ.lrn"), created.ProjectFilePath);
        Assert.True(File.Exists(created.ProjectFilePath));
        Assert.False(File.Exists(Path.Combine(_testRoot, "project.kfp")));
        Assert.True(Directory.Exists(Path.Combine(_testRoot, "media", "audio")));
        Assert.True(Directory.Exists(Path.Combine(_testRoot, "cache", "waveforms")));
        Assert.True(Directory.Exists(Path.Combine(_testRoot, "exports")));
    }

    [Fact]
    public async Task ImportCopiesSourceIntoWorkspaceAndUsesRelativePath()
    {
        string sourceDirectory = Path.Combine(_testRoot, "source");
        string workspaceDirectory = Path.Combine(_testRoot, "workspace");
        Directory.CreateDirectory(sourceDirectory);
        string sourcePath = Path.Combine(sourceDirectory, "song.mp3");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);

        var repository = new JsonProjectRepository();
        var service = new WorkspaceService(repository, new StubMetadataReader());
        WorkspaceSession session = await service.CreateAsync(workspaceDirectory, "Song");

        session = await service.ImportMediaAsync(session, sourcePath, MediaKind.Audio);
        File.Delete(sourcePath);

        MediaAsset asset = Assert.Single(session.Project.MediaAssets);
        Assert.Equal(asset.Id, session.Project.MasterAudioAssetId);
        Assert.Equal("media/audio/song.mp3", asset.RelativePath);
        Assert.True(File.Exists(Path.Combine(workspaceDirectory, "media", "audio", "song.mp3")));
        Assert.Equal(64, asset.ContentHash?.Length);
    }

    [Fact]
    public async Task ImportingReplacementSong_PersistsTheSameMasterThatWillBePlayed()
    {
        string workspaceDirectory = Path.Combine(_testRoot, "workspace");
        string sourceDirectory = Path.Combine(_testRoot, "source");
        Directory.CreateDirectory(sourceDirectory);
        string sourcePath = Path.Combine(sourceDirectory, "song.mp3");
        await File.WriteAllBytesAsync(sourcePath, [1]);
        var service = new WorkspaceService(new JsonProjectRepository(), new StubMetadataReader());
        WorkspaceSession session = await service.CreateAsync(workspaceDirectory, "Replacement");
        session = await service.ImportMediaAsync(session, sourcePath, MediaKind.Audio);
        Guid? oldMaster = session.Project.MasterAudioAssetId;
        session = await service.ImportMediaAsync(session, sourcePath, MediaKind.Audio);
        session = await service.SaveAsync(session);
        WorkspaceSession reopened = await service.OpenAsync(workspaceDirectory);

        Assert.NotEqual(oldMaster, reopened.Project.MasterAudioAssetId);
        Assert.Equal(session.Project.MediaAssets[^1].Id, reopened.Project.MasterAudioAssetId);
        Assert.Equal(2, reopened.Project.MediaAssets.Count);
    }

    [Fact]
    public async Task ImportedVideosAreAppendedAndOnlySongVideoKeepsAudioByDefault()
    {
        string sourceDirectory = Path.Combine(_testRoot, "source");
        string workspaceDirectory = Path.Combine(_testRoot, "workspace");
        Directory.CreateDirectory(sourceDirectory);
        string first = Path.Combine(sourceDirectory, "song.mp4");
        string second = Path.Combine(sourceDirectory, "background.mp4");
        await File.WriteAllBytesAsync(first, [1]);
        await File.WriteAllBytesAsync(second, [2]);

        var repository = new JsonProjectRepository();
        var service = new WorkspaceService(repository, new StubMetadataReader());
        WorkspaceSession session = await service.CreateAsync(workspaceDirectory, "Video song");

        session = await service.ImportMediaAsync(session, first, MediaKind.Video);
        session = await service.ImportMediaAsync(session, second, MediaKind.Video);

        Assert.False(session.Project.VideoClips[0].IsMuted);
        Assert.True(session.Project.VideoClips[1].IsMuted);
        Assert.Equal(
            session.Project.VideoClips[0].TimelineEnd,
            session.Project.VideoClips[1].TimelineStart);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private sealed class StubMetadataReader : IMediaMetadataReader
    {
        public Task<MediaProbeResult> ReadAsync(
            string filePath,
            MediaKind kind,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaProbeResult(
                ProjectDuration.FromSeconds(10),
                kind == MediaKind.Video ? 1920 : null,
                kind == MediaKind.Video ? 1080 : null,
                kind == MediaKind.Video ? 30 : null));
    }
}
