namespace KaraokeFilmMaker.Application.Separation;

public enum SeparationEngine { Cpu, Gpu }
public enum SeparationOutput { Accompaniment, SubtractVocals, Vocals }
public sealed record SeparationProgress(string Message, double? Fraction = null);
public sealed record GpuDevice(int Index, string Name, int ComputeMajor, int ComputeMinor, int DriverApiVersion);
public sealed record GpuAvailability(bool Supported, string Reason, GpuDevice? Device = null);

/// <summary>Conservative support contract for the pinned Windows CUDA 12.8 build.</summary>
public static class GpuSupportPolicy
{
    public static GpuAvailability Evaluate(IEnumerable<GpuDevice> devices)
    {
        var candidates = devices.ToArray();
        // Turing and newer, restricted to architecture families present in this wheel.
        bool Architecture(GpuDevice d) => (d.ComputeMajor, d.ComputeMinor) is
            (7, 5) or (8, 0) or (8, 6) or (8, 7) or (8, 9) or (9, 0) or (10, 0) or (12, 0);
        var device = candidates.FirstOrDefault(d => Architecture(d) && d.DriverApiVersion >= 12080);
        if (device is not null) return new(true, $"可启用 GPU 加速：{device.Name}", device);
        if (candidates.Length == 0) return new(false, "未检测到可用 NVIDIA 显卡。AMD / Intel 显卡暂不支持此加速包，请使用 CPU。");
        if (!candidates.Any(Architecture)) return new(false, "显卡架构过旧或尚未验证。首版支持列表从 NVIDIA Turing（计算能力 7.5）开始，请使用 CPU。");
        return new(false, "NVIDIA 驱动过旧：此组件要求驱动支持 CUDA 12.8。请通过 NVIDIA 官方渠道更新驱动后重新检测。", candidates.First(Architecture));
    }
}

public sealed record SeparationRequest(string SourcePath, string OutputDirectory, SeparationEngine Engine,
    SeparationOutput Output, int GpuIndex = 0);

public interface IVocalSeparationService
{
    Task<string> SeparateAsync(SeparationRequest request, IProgress<SeparationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
