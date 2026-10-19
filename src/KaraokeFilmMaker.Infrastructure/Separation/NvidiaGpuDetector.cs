using System.Runtime.InteropServices;
using System.Text;
using KaraokeFilmMaker.Application.Separation;

namespace KaraokeFilmMaker.Infrastructure.Separation;

public static class NvidiaGpuDetector
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Init(uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GetInt(out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GetDevice(out int device, int ordinal);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GetName(StringBuilder name, int length, int device);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GetCapability(out int major, out int minor, int device);

    public static GpuAvailability Detect()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            return new(false, "GPU 加速仅支持 Windows x64。");
        nint library = 0;
        try
        {
            // Never search a project folder or PATH for a native executable library.
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvcuda.dll");
            if (!NativeLibrary.TryLoad(path, out library)) return GpuSupportPolicy.Evaluate([]);
            T Function<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            if (Function<Init>("cuInit")(0) != 0) return new(false, "NVIDIA 驱动未能初始化。请检查驱动后重新检测；仍可使用 CPU。");
            if (Function<GetInt>("cuDeviceGetCount")(out int count) != 0 || Function<GetInt>("cuDriverGetVersion")(out int driver) != 0)
                return new(false, "无法读取 NVIDIA 显卡信息；为避免安装不兼容组件，暂时禁用 GPU 加速。");
            List<GpuDevice> devices = [];
            for (int i = 0; i < Math.Min(count, 32); i++)
            {
                var name = new StringBuilder(256);
                if (Function<GetDevice>("cuDeviceGet")(out int device, i) == 0
                    && Function<GetName>("cuDeviceGetName")(name, 256, device) == 0
                    && Function<GetCapability>("cuDeviceComputeCapability")(out int major, out int minor, device) == 0)
                    devices.Add(new(i, name.ToString(), major, minor, driver));
            }
            return GpuSupportPolicy.Evaluate(devices);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return new(false, "NVIDIA 驱动接口不可用，请更新驱动或继续使用 CPU。"); }
        finally { if (library != 0) NativeLibrary.Free(library); }
    }
}
