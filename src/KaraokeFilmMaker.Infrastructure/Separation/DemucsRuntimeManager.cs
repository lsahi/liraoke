using System.Net.Http;
using System.Text.Json;
using KaraokeFilmMaker.Application.Separation;
using KaraokeFilmMaker.Infrastructure.Media;

namespace KaraokeFilmMaker.Infrastructure.Separation;

public sealed record CpuRuntimeInfo(string TorchVersion, string DemucsVersion, string ModelSha256);

public sealed class DemucsRuntimeManager
{
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    public GpuRuntimeManifest Manifest { get; } = GpuRuntimeManifest.Load();
    public string BundlePath { get; }
    public string ComponentPath { get; }
    public string CpuPath => Path.Combine(BundlePath, "cpu");
    public string ModelPath => Path.Combine(BundlePath, "model", "955717e8.safetensors");
    public string WorkerPath => Path.Combine(BundlePath, "demucs-worker.py");
    private string ActiveFile => Path.Combine(ComponentPath, "active.json");
    public bool CpuInstalled => File.Exists(Path.Combine(CpuPath, "python.exe")) && File.Exists(ModelPath)
        && File.Exists(WorkerPath) && File.Exists(Path.Combine(CpuPath, "runtime.json"));

    public DemucsRuntimeManager(string? bundlePath = null, string? componentPath = null)
    {
        BundlePath = bundlePath ?? FindBundle();
        ComponentPath = Path.GetFullPath(componentPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaraokeFilmMaker", "Components", "Demucs"));
    }

    private static string FindBundle()
    {
        foreach (var initial in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            DirectoryInfo? directory = new(initial);
            for (int i = 0; directory is not null && i < 8; i++, directory = directory.Parent)
            {
                string path = Path.Combine(directory.FullName, "tools", "demucs");
                if (File.Exists(Path.Combine(path, "cpu", "runtime.json"))) return path;
            }
        }
        return Path.Combine(AppContext.BaseDirectory, "tools", "demucs");
    }

    public string? InstalledGpuPath
    {
        get
        {
            try
            {
                string? leaf = JsonSerializer.Deserialize<string>(File.ReadAllText(ActiveFile));
                if (leaf is null || leaf.Length != Manifest.Id.Length + 33
                    || !leaf.StartsWith(Manifest.Id + "-", StringComparison.Ordinal)
                    || !Guid.TryParseExact(leaf[(Manifest.Id.Length + 1)..], "N", out _)) return null;
                string path = Path.Combine(ComponentPath, leaf);
                var info = JsonSerializer.Deserialize<CpuRuntimeInfo>(File.ReadAllText(Path.Combine(path, "runtime.json")));
                return info?.TorchVersion == Manifest.TorchVersion && File.Exists(Path.Combine(path, "python.exe")) ? path : null;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
        }
    }

    public IDisposable AcquireOperationLock()
    {
        Directory.CreateDirectory(ComponentPath);
        try { return new FileStream(Path.Combine(ComponentPath, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new IOException("另一个 Liraoke 正在使用或安装人声分离组件，请稍后重试。", e); }
    }

    public async Task InstallGpuAsync(GpuAvailability gpu, IProgress<SeparationProgress>? progress, CancellationToken token)
    {
        if (!gpu.Supported || gpu.Device is null) throw new InvalidOperationException(gpu.Reason);
        if (!CpuInstalled) throw new InvalidOperationException("缺少随软件提供的 CPU 基础组件，请重新安装完整 Liraoke 包。");
        using var operation = AcquireOperationLock();
        var drive = new DriveInfo(Path.GetPathRoot(ComponentPath)!);
        if (!drive.IsReady || drive.AvailableFreeSpace < Manifest.TemporarySpaceBytes)
            throw new IOException("安装 GPU 组件需要至少 12 GiB 可用临时空间（包含下载、解压及修复时保留旧版本）。");
        string cache = Path.Combine(ComponentPath, "downloads", Manifest.WheelSha256 + ".whl");
        string wheel = await new VerifiedComponentDownload(Client).DownloadAsync(new Uri(Manifest.WheelUrl),
            Manifest.WheelBytes, Manifest.WheelSha256, cache, progress, token).ConfigureAwait(false);
        string staging = Path.Combine(ComponentPath, Manifest.Id + "-" + Guid.NewGuid().ToString("N"));
        string? old = InstalledGpuPath;
        bool activated = false;
        try
        {
            progress?.Report(new("安装独立 GPU 运行环境…", .70));
            await Task.Run(() => CopyCpuBase(CpuPath, staging, token), token).ConfigureAwait(false);
            await SafeRuntimeArchive.ExtractWheelAsync(wheel, Path.Combine(staging, "Lib", "site-packages"), token).ConfigureAwait(false);
            progress?.Report(new("执行真实模型自检…", .93));
            await SelfTestAsync(staging, SeparationEngine.Gpu, gpu.Device.Index, token).ConfigureAwait(false);
            var cpuInfo = ReadCpuInfo();
            await File.WriteAllTextAsync(Path.Combine(staging, "runtime.json"),
                JsonSerializer.Serialize(cpuInfo with { TorchVersion = Manifest.TorchVersion }), token).ConfigureAwait(false);
            string temporaryPointer = ActiveFile + ".new";
            await File.WriteAllTextAsync(temporaryPointer, JsonSerializer.Serialize(Path.GetFileName(staging)), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporaryPointer, ActiveFile, true);
            activated = true;
            if (old is not null) TryDeleteManagedDirectory(old);
            // Installation succeeded; no need to keep a second multi-GB wheel.
            try { File.Delete(cache); } catch (IOException) { }
            progress?.Report(new("GPU 加速已安装并通过自检。", 1));
        }
        finally { if (!activated) TryDeleteManagedDirectory(staging); }
    }

    public void UninstallGpu()
    {
        using var operation = AcquireOperationLock();
        string? old = InstalledGpuPath;
        if (File.Exists(ActiveFile)) File.Delete(ActiveFile);
        if (old is not null) DeleteManagedDirectory(old);
    }

    private void TryDeleteManagedDirectory(string path)
    {
        try { DeleteManagedDirectory(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void DeleteManagedDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), ComponentPath, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith(Manifest.Id + "-", StringComparison.Ordinal))
            throw new InvalidOperationException("拒绝清理组件目录之外的文件。");
        if (!Directory.Exists(full)) return;
        if (Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories)
            .Prepend(full).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("组件目录存在链接，拒绝递归清理。");
        Directory.Delete(full, true);
    }

    private static void CopyCpuBase(string source, string target, CancellationToken token)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (relative == "runtime.json") continue;
            if (parts.Length >= 3 && parts[0] == "Lib" && parts[1] == "site-packages"
                && (parts[2] is "torch" or "torchgen" or "functorch" || parts[2].StartsWith("torch-", StringComparison.Ordinal))) continue;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("基础组件包含未解析链接。");
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, false);
        }
    }

    private CpuRuntimeInfo ReadCpuInfo()
    {
        var info = JsonSerializer.Deserialize<CpuRuntimeInfo>(File.ReadAllText(Path.Combine(CpuPath, "runtime.json")))!;
        if (info.TorchVersion != "2.8.0+cpu" || info.DemucsVersion != "4.1.0"
            || info.ModelSha256.Length != 64 || !info.ModelSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("CPU 基础组件与当前程序不匹配。");
        return info;
    }

    public List<string> WorkerArguments(SeparationEngine engine, int gpuIndex)
    {
        var info = ReadCpuInfo();
        return ["-I", "-X", "utf8", WorkerPath, "--model", ModelPath, "--model-sha256", info.ModelSha256,
            "--device", engine == SeparationEngine.Gpu ? "cuda" : "cpu", "--gpu-index", gpuIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--expected-torch", engine == SeparationEngine.Gpu ? Manifest.TorchVersion : info.TorchVersion];
    }

    public async Task SelfTestAsync(string runtime, SeparationEngine engine, int gpuIndex, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var args = WorkerArguments(engine, gpuIndex);
        args.Add("--self-test");
        try { await MediaProcess.RunAsync(Path.Combine(runtime, "python.exe"), args, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException e) when (!token.IsCancellationRequested)
        { throw new TimeoutException("模型自检超时，组件未启用。请使用 CPU 或检查 GPU 驱动。", e); }
    }
}
