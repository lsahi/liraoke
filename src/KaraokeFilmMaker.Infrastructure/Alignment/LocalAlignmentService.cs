using System.Text;
using System.Diagnostics;
using System.Net.Http;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using KaraokeFilmMaker.Application.Alignment;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Infrastructure.Separation;
using KaraokeFilmMaker.Application.Separation;
using KaraokeFilmMaker.Infrastructure.Media;

namespace KaraokeFilmMaker.Infrastructure.Alignment;

public sealed record AlignmentHardware(long RamGiB, long AvailableRamGiB, long FreeDiskGiB, bool WindowsX64, bool GpuSupported, string GpuReason, long GpuGiB, int GpuIndex);
public sealed record AlignmentPluginStatus(AlignmentPlugin Plugin, bool Installed, string? Device, bool CanInstallCpu, bool CanInstallGpu, string Message);
public sealed class LocalAlignmentService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    public string Root { get; }
    public LocalAlignmentService(string? root = null) => Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaraokeFilmMaker", "Components", "Alignment"));
    private static string Resource(string name)
    {
        using var stream = typeof(LocalAlignmentService).Assembly.GetManifestResourceStream("KaraokeFilmMaker.Infrastructure.Alignment.Runtime." + name) ?? throw new IOException("缺少打轴插件资源：" + name);
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    public static string InstallGuide => Resource("SKILL.md");
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended; }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int DeviceMemory(out nuint bytes, int device);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int DeviceGet(out int device, int ordinal);
    public AlignmentHardware Hardware()
    {
        const long GiB=1024L*1024*1024; var memory=new MemoryStatus { Length=(uint)Marshal.SizeOf<MemoryStatus>() };
        bool windows=OperatingSystem.IsWindowsVersionAtLeast(10,0,17763) && RuntimeInformation.ProcessArchitecture==Architecture.X64;
        if (windows) GlobalMemoryStatusEx(ref memory);
        long free = new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace/GiB;
        var gpu=NvidiaGpuDetector.Detect(); long video=0; nint library=0;
        try
        {
            if (gpu.Supported && NativeLibrary.TryLoad(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"nvcuda.dll"),out library))
            {
                var get=Marshal.GetDelegateForFunctionPointer<DeviceGet>(NativeLibrary.GetExport(library,"cuDeviceGet"));
                var mem=Marshal.GetDelegateForFunctionPointer<DeviceMemory>(NativeLibrary.GetExport(library,"cuDeviceTotalMem_v2"));
                if(get(out int device,gpu.Device!.Index)==0 && mem(out var bytes,device)==0)video=(long)Math.Round((double)bytes/GiB);
            }
        }
        catch(Exception e) when(e is EntryPointNotFoundException or BadImageFormatException) { }
        finally { if(library!=0)NativeLibrary.Free(library); }
        return new((long)Math.Round((double)memory.TotalPhysical/GiB),(long)memory.AvailablePhysical/GiB,free,windows,gpu.Supported&&video>0,gpu.Reason,video,gpu.Device?.Index??0);
    }
    private string? Active(string id)
    {
        AlignmentPlugins.Get(id);
        try
        {
            string leaf=File.ReadAllText(Path.Combine(Root,id+".active")).Trim();
            if(!leaf.StartsWith(id+"-",StringComparison.Ordinal)||!Guid.TryParseExact(leaf[(id.Length+1)..],"N",out _))return null;
            string path=Path.Combine(Root,leaf);var state=JsonDocument.Parse(File.ReadAllText(Path.Combine(path,"installed.json"))).RootElement;
            var spec=JsonDocument.Parse(Resource("alignment-manifest.json")).RootElement.GetProperty(id);
            return state.GetProperty("selfTestPassed").GetBoolean() && state.GetProperty("plugin").GetString()==id && state.GetProperty("revision").GetString()==spec.GetProperty("revision").GetString()
                && state.GetProperty("device").GetString() is ("cpu" or "gpu")
                && File.Exists(Path.Combine(path,"python","python.exe")) ? path : null;
        }
        catch(Exception e) when(e is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }
    public IReadOnlyList<AlignmentPluginStatus> Status()
    {
        var h=Hardware(); return AlignmentPlugins.All.Select(p=>
        {
            var path=Active(p.Id);string? device=path is null?null:JsonDocument.Parse(File.ReadAllText(Path.Combine(path,"installed.json"))).RootElement.GetProperty("device").GetString();
            bool cpu=h.WindowsX64&&h.RamGiB>=p.MinimumRamGiB&&h.FreeDiskGiB>=10;
            bool gpu=cpu&&h.FreeDiskGiB>=20&&h.GpuSupported&&h.GpuGiB>=p.MinimumGpuGiB;
            string message=$"内存 {h.RamGiB} GiB（可用 {h.AvailableRamGiB}），磁盘可用 {h.FreeDiskGiB} GiB；建议至少 {p.MinimumRamGiB} GiB 内存。CPU 安装预留 10 GiB，GPU 预留 20 GiB。\n{h.GpuReason}；显存 {h.GpuGiB} GiB，GPU 模式要求至少 {p.MinimumGpuGiB} GiB。";
            return new AlignmentPluginStatus(p,path is not null,device,cpu,gpu,message);
        }).ToArray();
    }
    private IDisposable Lock()
    {
        Directory.CreateDirectory(Root);
        try{return new FileStream(Path.Combine(Root,"operation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){throw new InvalidOperationException("另一个安装或打轴任务正在运行，请稍后重试。");}
    }
    private string WriteResource(string directory,string name) {string path=Path.Combine(directory,name);File.WriteAllText(path,Resource(name));return path;}
    public async Task<AlignmentCacheImportResult> ImportDownloadsAsync(IEnumerable<string> files,IProgress<string>? progress,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var operation=Lock();
        var cache=new AlignmentDownloadCache(Path.Combine(Root,"cache","downloads"),AlignmentDownloadCache.ReadCatalog(Resource("alignment-manifest.json")));
        return await cache.ImportAsync(files,progress,token);
    }
    public async Task InstallAsync(string id,string device,IProgress<string>? progress,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(device is not ("cpu" or "gpu"))throw new InvalidOperationException("请选择 CPU 或 GPU。");
        var status=Status().Single(s=>s.Plugin.Id==AlignmentPlugins.Get(id).Id);
        if(!(device=="gpu"?status.CanInstallGpu:status.CanInstallCpu))throw new InvalidOperationException("配置检查未通过。"+status.Message);
        using var operation=Lock(); string folder=Path.Combine(Root,id+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string log=Path.Combine(folder,"install.log"); progress?.Report("下载独立 Python 运行环境…");
        try
        {
            var manifest=JsonDocument.Parse(Resource("alignment-manifest.json")).RootElement;
            var spec=manifest.GetProperty("python");string zip=Path.Combine(folder,"python.zip");
            var pythonSpec=new AlignmentDownloadSpec(spec.GetProperty("sha256").GetString()!,spec.GetProperty("bytes").GetInt64());
            string cacheDirectory=Path.Combine(Root,"cache","downloads"),cachedPython=Path.Combine(cacheDirectory,pythonSpec.Sha256);
            if(await AlignmentDownloadCache.VerifiedAsync(cachedPython,pythonSpec,token))
            {
                progress?.Report("复用已校验的 Python 运行环境下载。");
                await using var input=File.OpenRead(cachedPython);await using var output=File.Create(zip);await input.CopyToAsync(output,token);
            }
            else
            {
                using(var response=await Http.GetAsync(spec.GetProperty("url").GetString(),HttpCompletionOption.ResponseHeadersRead,token))
                {response.EnsureSuccessStatusCode();await using var file=File.Create(zip);await response.Content.CopyToAsync(file,token);}
                if(!await AlignmentDownloadCache.VerifiedAsync(zip,pythonSpec,token))throw new IOException("Python 下载校验失败。");
                await new AlignmentDownloadCache(cacheDirectory,[pythonSpec]).ImportAsync([zip],progress,token);
            }
            string pythonDir=Path.Combine(folder,"python");ZipFile.ExtractToDirectory(zip,pythonDir);File.Delete(zip);
            File.WriteAllText(Path.Combine(pythonDir,"python312._pth"),"python312.zip\n.\nLib/site-packages\nimport site\n");
            WriteResource(folder,"ALIGNMENT-NOTICES.txt");
            foreach(string license in new[]{"CC-BY-NC-SA-4.0.txt","CC-BY-NC-4.0.txt","APACHE-2.0.txt"})WriteResource(folder,license);
            string installer=WriteResource(folder,"install-alignment.py"),worker=WriteResource(folder,"alignment-worker.py"),manifestFile=WriteResource(folder,"alignment-manifest.json");
            await RunAsync(Path.Combine(pythonDir,"python.exe"),[installer,"--plugin",id,"--device",device,"--runtime",folder,"--manifest",manifestFile,"--worker",worker],log,progress,token,device=="gpu"?Hardware().GpuIndex:null);
            token.ThrowIfCancellationRequested();
            string pointer=Path.Combine(Root,id+".active"),temporary=pointer+".new";File.WriteAllText(temporary,Path.GetFileName(folder));File.Move(temporary,pointer,true);
            progress?.Report("插件已安装并通过模型推理自检。");
        }
        catch(Exception e) when(e is not OperationCanceledException) {throw new IOException("插件安装未完成，旧安装仍可使用。日志："+log+"\n"+e.Message,e);}
    }
    public async Task<AlignmentOutcome> AlignAsync(string id,string audio,string? lrc,LyricDocument document,MediaToolchain mediaTools,IProgress<string>? progress,CancellationToken token)
    {
        string runtime=Active(id)??throw new InvalidOperationException("请先安装所选打轴插件。");
        using var operation=Lock();
        string job=Path.Combine(Root,"jobs",DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(job);
        string log=Path.Combine(job,"alignment.log"),wav=Path.Combine(job,"audio.wav"),request=Path.Combine(job,"request.json"),output=Path.Combine(job,"result.json");
        string device=JsonDocument.Parse(File.ReadAllText(Path.Combine(runtime,"installed.json"))).RootElement.GetProperty("device").GetString()!;
        if(device=="gpu" && !Hardware().GpuSupported)throw new InvalidOperationException("当前 GPU 不可用，请在插件管理中重新安装 CPU 模式。");
        try
        {
            var demucs = new DemucsRuntimeManager();
            var gpu = NvidiaGpuDetector.Detect();
            var separationEngine = demucs.InstalledGpuPath is not null && gpu.Supported ? SeparationEngine.Gpu : SeparationEngine.Cpu;
            var logGate = new object();
            var preparationProgress = new ForwardProgress<string>(message =>
            {
                lock (logGate) File.AppendAllText(log, message + Environment.NewLine, Encoding.UTF8);
                progress?.Report(message);
            });
            await new AlignmentAudioPreparation(new DemucsSeparationService(demucs, mediaTools), mediaTools)
                .PrepareAsync(audio, job, separationEngine, gpu.Device?.Index ?? 0, preparationProgress, token);
            progress?.Report("使用已提取人声定位句段并对齐；保留已校正读音…");
            var lines=document.Lines.Where(l=>!l.IsInstrumental&&l.Units.Count>0).Select(l=>new { id=l.Id,text=l.PrimaryText,@protected=AlignmentEdits.Protected(l),units=l.Units.Select(u=>
            {
                string surface=string.Concat(u.DisplayRanges.Select(r=>l.PrimaryText.Substring(r.Start,r.Length)));
                string lang=u.LanguageId is "ja" or "zh" or "en"?u.LanguageId:u.Reading.Any(c=>c is >= '\u3040' and <= '\u30ff')?"ja":u.Reading.Any(char.IsAsciiLetter)?"en":"zh";
                string reading=lang=="ja"&&surface==u.Reading?u.Reading switch{"は"=>"わ","へ"=>"え",_=>u.Reading}:u.Reading;
                return new{id=u.Id,reading,surface,language=lang, fixedStart=(u.IsConfirmed || u.RawInputTime is not null) ? (double?)u.Timing.Start.TotalSeconds : null, fixedEnd=(u.IsConfirmed || u.RawInputTime is not null) ? (double?)u.Timing.End.TotalSeconds : null};
            }).ToArray()}).ToArray();
            await File.WriteAllTextAsync(request,JsonSerializer.Serialize(new{audio=wav,audioRole="vocals",separationModel="htdemucs",lrc,lines},Json),token);
            string worker=WriteResource(job,"alignment-worker.py");
            await RunAsync(Path.Combine(runtime,"python","python.exe"),[worker,"--runtime",runtime,"--plugin",id,"--device",device,"--request",request,"--output",output],log,progress,token,device=="gpu"?Hardware().GpuIndex:null);
            var result=JsonDocument.Parse(await File.ReadAllTextAsync(output,token)).RootElement;
            return new(result.GetProperty("lines").Deserialize<AlignmentLineResult[]>(Json)??[],log);
        }
        catch (OperationCanceledException)
        {
            File.AppendAllText(log, "打轴已取消。" + Environment.NewLine, Encoding.UTF8);
            throw;
        }
        catch (Exception error)
        {
            File.AppendAllText(log, error.ToString() + Environment.NewLine, Encoding.UTF8);
            throw new InvalidOperationException("本地打轴失败：" + error.Message + "\n日志：" + log, error);
        }
        finally { if(File.Exists(wav))File.Delete(wav); }
    }
    private static async Task RunAsync(string executable,IEnumerable<string> arguments,string log,IProgress<string>? progress,CancellationToken token,int? gpuIndex=null)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(var arg in arguments)start.ArgumentList.Add(arg);
        if(gpuIndex is {} index)start.Environment["CUDA_VISIBLE_DEVICES"]=index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["PYTHONUTF8"]="1";start.Environment["PYTHONIOENCODING"]="utf-8";start.Environment["PYTHONUNBUFFERED"]="1";start.Environment["PYTHONNOUSERSITE"]="1";
        using var process=Process.Start(start)??throw new IOException("无法启动本地插件进程。");
        using var registration=token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){} });
        using var writer=new StreamWriter(log,append:true);var gate=new SemaphoreSlim(1);
        async Task Drain(StreamReader reader)
        {
            while(await reader.ReadLineAsync() is {} line)
            {
                await gate.WaitAsync();try{await writer.WriteLineAsync(line);await writer.FlushAsync();}finally{gate.Release();}
                if(line.StartsWith('{'))try{var value=JsonDocument.Parse(line).RootElement;if(value.TryGetProperty("message",out var m))progress?.Report(m.GetString()??"");}catch(JsonException){}
                else if(line.StartsWith("Progress ") && line.Split(' ') is [_,var current,_,var maximum] && long.TryParse(current,out long count) && long.TryParse(maximum,out long total))progress?.Report($"依赖下载：{count/1048576d:F1} / {total/1048576d:F1} MiB");
                else if(line.Contains("Downloading ") || line.Contains("Installing collected") || line.Contains("Successfully installed") || line.Contains("Resuming download") || line.Contains(" MB"))progress?.Report(line.Trim());
            }
        }
        await Task.WhenAll(Drain(process.StandardOutput),Drain(process.StandardError),process.WaitForExitAsync());token.ThrowIfCancellationRequested();
        if(process.ExitCode!=0)throw new IOException("本地进程失败，请查看日志："+log);
    }
}
