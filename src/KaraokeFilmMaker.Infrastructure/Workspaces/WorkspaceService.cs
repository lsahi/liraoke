using System.Security.Cryptography;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Application.Timeline;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Timeline;

namespace KaraokeFilmMaker.Infrastructure.Workspaces;

public sealed class WorkspaceService(
    IProjectRepository projectRepository,
    IMediaMetadataReader metadataReader) : IWorkspaceService
{
    private static readonly string[] WorkspaceDirectories =
    [
        "media/audio",
        "media/video",
        "media/images",
        "lyrics",
        "cache/waveforms",
        "cache/thumbnails",
        "cache/proxies",
        "exports",
    ];

    public async Task<WorkspaceSession> CreateAsync(
        string workspacePath,
        string projectTitle,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectTitle);

        string rootPath = Path.GetFullPath(workspacePath);
        if (Directory.Exists(rootPath) && Directory.EnumerateFileSystemEntries(rootPath).Any())
        {
            throw new InvalidOperationException("新工作区必须使用空文件夹。");
        }

        Directory.CreateDirectory(rootPath);
        foreach (string relativeDirectory in WorkspaceDirectories)
        {
            Directory.CreateDirectory(ToAbsolutePath(rootPath, relativeDirectory));
        }

        KaraokeProject project = KaraokeProject.Create(projectTitle, DateTimeOffset.UtcNow);
        string projectFile = WorkspaceProjectPath.CreateEntry(rootPath, projectTitle);
        await projectRepository.SaveAsync(projectFile, project, cancellationToken).ConfigureAwait(false);
        return new WorkspaceSession(rootPath, project, projectFile);
    }

    public async Task<WorkspaceSession> OpenAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        string projectFile = WorkspaceProjectPath.Resolve(workspacePath);
        string rootPath = Path.GetDirectoryName(projectFile)!;
        KaraokeProject project = await projectRepository
            .LoadAsync(projectFile, cancellationToken)
            .ConfigureAwait(false);
        return new WorkspaceSession(rootPath, project, projectFile);
    }

    public async Task<WorkspaceSession> SaveAsync(
        WorkspaceSession session,
        CancellationToken cancellationToken = default)
    {
        var updatedProject = session.Project with { ModifiedAtUtc = DateTimeOffset.UtcNow };
        await projectRepository
            .SaveAsync(session.ProjectFilePath ?? session.RootPath, updatedProject, cancellationToken)
            .ConfigureAwait(false);
        return session with { Project = updatedProject };
    }

    public async Task<WorkspaceSession> ImportMediaAsync(
        WorkspaceSession session,
        string sourcePath,
        MediaKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("找不到要导入的素材。", fullSourcePath);
        }

        string relativeDirectory = kind switch
        {
            MediaKind.Audio => "media/audio",
            MediaKind.Video => "media/video",
            MediaKind.Image => "media/images",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        string destinationDirectory = ToAbsolutePath(session.RootPath, relativeDirectory);
        Directory.CreateDirectory(destinationDirectory);
        string destinationPath = GetUniqueDestinationPath(destinationDirectory, Path.GetFileName(fullSourcePath));
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destinationDirectory))!);
        if (drive.IsReady && drive.AvailableFreeSpace < new FileInfo(fullSourcePath).Length + 20 * 1024 * 1024)
        {
            throw new IOException("工作区所在磁盘空间不足。");
        }
        bool copyCompleted = false;

        try
        {
            await CopyFileAsync(fullSourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
            copyCompleted = true;

            MediaProbeResult metadata = await metadataReader
                .ReadAsync(destinationPath, kind, cancellationToken)
                .ConfigureAwait(false);
            if (kind != MediaKind.Image && metadata.Duration.Microseconds <= 0)
            {
                throw new InvalidDataException("所选文件没有有效的音频或视频时长；静态图片请使用“导入图片”。");
            }
            if ((kind == MediaKind.Audio && metadata.HasAudio == false)
                || (kind is MediaKind.Video or MediaKind.Image && (metadata.Width is null || metadata.Height is null)))
            {
                throw new InvalidDataException("所选文件没有需要的音频或视频流。");
            }
            string contentHash = await ComputeSha256Async(destinationPath, cancellationToken).ConfigureAwait(false);
            string relativePath = Path.GetRelativePath(session.RootPath, destinationPath).Replace('\\', '/');

            var asset = new MediaAsset(
                Guid.NewGuid(),
                Path.GetFileName(fullSourcePath),
                relativePath,
                kind,
                kind == MediaKind.Image ? ProjectDuration.Zero : metadata.Duration,
                contentHash);

            List<MediaAsset> assets = [.. session.Project.MediaAssets, asset];
            List<VideoClip> clips = [.. session.Project.VideoClips];
            Guid? masterAudioId = session.Project.MasterAudioAssetId;

            if (kind == MediaKind.Audio)
            {
                masterAudioId = asset.Id;
            }
            else if (kind is MediaKind.Video or MediaKind.Image)
            {
                bool isSongVideo = kind == MediaKind.Video && masterAudioId is null && metadata.HasAudio != false;
                if (isSongVideo)
                {
                    masterAudioId = asset.Id;
                }

                var timelineStart = clips.Count == 0
                    ? ProjectTime.Zero
                    : new ProjectTime(clips.Max(clip => clip.TimelineEnd.Microseconds));
                clips.Add(new VideoClip(
                    Guid.NewGuid(),
                    asset.Id,
                    TimelineStart: timelineStart,
                    SourceStart: ProjectTime.Zero,
                    Duration: kind == MediaKind.Image ? VideoTimelineService.DefaultImageDuration : metadata.Duration,
                    IsMuted: !isSongVideo));
            }

            var updatedProject = session.Project with
            {
                ModifiedAtUtc = DateTimeOffset.UtcNow,
                MasterAudioAssetId = masterAudioId,
                WorkArea = masterAudioId == session.Project.MasterAudioAssetId ? session.Project.WorkArea : null,
                MediaAssets = assets,
                VideoClips = clips,
            };

            return session with { Project = updatedProject };
        }
        catch
        {
            if (copyCompleted && File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            throw;
        }
    }

    private static string ToAbsolutePath(string rootPath, string relativePath) =>
        Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string GetUniqueDestinationPath(string destinationDirectory, string sourceFileName)
    {
        string fileName = Path.GetFileNameWithoutExtension(sourceFileName);
        string extension = Path.GetExtension(sourceFileName);
        string candidate = Path.Combine(destinationDirectory, sourceFileName);

        for (var suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(destinationDirectory, $"{fileName} ({suffix}){extension}");
        }

        return candidate;
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        bool created = false;
        try
        {
            await using FileStream destination = new(destinationPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
            created = true;
            await source.CopyToAsync(destination, 128 * 1024, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (created) { File.Delete(destinationPath); }
            throw;
        }
    }

    private static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(filePath);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
