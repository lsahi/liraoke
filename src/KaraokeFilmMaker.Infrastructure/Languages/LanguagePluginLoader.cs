using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Infrastructure.Languages;

public enum LanguagePluginError
{
    InvalidFile,
    NoAdaptor,
    ConstructionFailed,
    IncompatibleApi,
    InvalidMetadata,
    DuplicateLanguage,
    UnsupportedCapability,
    DependencyFailure,
}

public sealed class LanguagePluginLoadException(LanguagePluginError code, string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public LanguagePluginError Code { get; } = code;
}

/// <summary>
/// Loads only a DLL explicitly selected by the user. Assembly isolation is NOT a security sandbox:
/// constructors and all other plugin code execute with the host application's permissions.
/// Keep this object alive for as long as its adaptors are in use.
/// </summary>
public sealed class LanguagePluginLoader : IDisposable
{
    private readonly List<PluginLoadContext> _contexts = [];
    private readonly HashSet<string> _languageIds = new(StringComparer.OrdinalIgnoreCase)
    {
        LanguageIds.Chinese, LanguageIds.Japanese, LanguageIds.English, LanguageIds.Unknown,
    };
    private bool _disposed;

    public LanguagePluginLoader(IEnumerable<string>? existingLanguageIds = null)
    {
        if (existingLanguageIds is not null) { _languageIds.UnionWith(existingLanguageIds); }
    }

    public IReadOnlyList<ILanguageAdaptor> Load(string dllPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(dllPath)
            || !string.Equals(Path.GetExtension(dllPath), ".dll", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(dllPath))
        {
            throw new LanguagePluginLoadException(LanguagePluginError.InvalidFile,
                "请选择存在的语言插件 DLL 文件。");
        }

        PluginLoadContext? context = null;
        try
        {
            string fullPath = Path.GetFullPath(dllPath);
            // Metadata inspection fails for native libraries and corrupt files before loading code.
            _ = AssemblyName.GetAssemblyName(fullPath);
            context = new PluginLoadContext(fullPath);
            Assembly assembly = context.LoadFromAssemblyPath(fullPath);
            var candidates = assembly.GetTypes().Where(type => type.IsClass && !type.IsAbstract
                && type.IsVisible && !type.ContainsGenericParameters
                && typeof(ILanguageAdaptor).IsAssignableFrom(type)).ToArray();
            if (candidates.Length == 0)
            {
                throw new LanguagePluginLoadException(LanguagePluginError.NoAdaptor,
                    "该 DLL 没有公开的语言适配器，请选择实现 ILanguageAdaptor 的插件主文件。");
            }

            List<ILanguageAdaptor> adaptors = [];
            foreach (Type candidate in candidates)
            {
                if (candidate.GetConstructor(Type.EmptyTypes) is null)
                {
                    throw new LanguagePluginLoadException(LanguagePluginError.ConstructionFailed,
                        $"插件入口 {candidate.Name} 需要公开的无参数构造函数。");
                }
                try
                {
                    adaptors.Add((ILanguageAdaptor)Activator.CreateInstance(candidate)!);
                }
                catch (Exception exception)
                {
                    throw new LanguagePluginLoadException(LanguagePluginError.ConstructionFailed,
                        $"语言插件 {candidate.Name} 初始化失败。", exception);
                }
            }

            ValidateAdaptors(adaptors, _languageIds);
            _languageIds.UnionWith(adaptors.Select(adaptor => adaptor.LanguageId));
            _contexts.Add(context);
            context = null;
            return adaptors.AsReadOnly();
        }
        catch (LanguagePluginLoadException) { throw; }
        catch (BadImageFormatException exception)
        {
            throw new LanguagePluginLoadException(LanguagePluginError.InvalidFile,
                "无法加载此 DLL：文件损坏、不是托管插件或处理器架构不兼容。", exception);
        }
        catch (Exception exception)
        {
            throw new LanguagePluginLoadException(LanguagePluginError.DependencyFailure,
                "语言插件加载失败。请确认使用兼容的 SDK，并把插件依赖保留在其 DLL 所在文件夹。", exception);
        }
        finally { context?.Unload(); }
    }

    /// <summary>Validates a complete batch before registration; also useful to plugin authors' tests.</summary>
    public static void ValidateAdaptors(IEnumerable<ILanguageAdaptor> adaptors,
        IEnumerable<string>? existingLanguageIds = null)
    {
        var ids = new HashSet<string>(existingLanguageIds ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (ILanguageAdaptor adaptor in adaptors)
        {
            if (adaptor.ApiVersion != BaseLanguageAdaptor.CurrentApiVersion)
            {
                throw new LanguagePluginLoadException(LanguagePluginError.IncompatibleApi,
                    $"插件 API {adaptor.ApiVersion} 不兼容；本程序要求 API {BaseLanguageAdaptor.CurrentApiVersion}。");
            }
            if (string.IsNullOrWhiteSpace(adaptor.LanguageId)
                || !Regex.IsMatch(adaptor.LanguageId, @"\A[a-zA-Z][a-zA-Z0-9]*(?:-[a-zA-Z0-9]+)*\z")
                || string.IsNullOrWhiteSpace(adaptor.DisplayName)
                || string.IsNullOrWhiteSpace(adaptor.Version)
                || adaptor.Policy is null || adaptor.Capabilities is null)
            {
                throw new LanguagePluginLoadException(LanguagePluginError.InvalidMetadata,
                    "语言插件必须提供有效的语言 ID、名称、版本、处理策略及能力声明。");
            }
            if (!ids.Add(adaptor.LanguageId))
            {
                throw new LanguagePluginLoadException(LanguagePluginError.DuplicateLanguage,
                    $"语言 ID「{adaptor.LanguageId}」已经存在，不能覆盖内置或已加载的语言包。");
            }
            if (adaptor.Capabilities.TextDirection != LyricTextDirection.LeftToRight)
            {
                throw new LanguagePluginLoadException(LanguagePluginError.UnsupportedCapability,
                    $"语言「{adaptor.DisplayName}」需要当前尚未支持的文字方向，暂不能加载。");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        foreach (PluginLoadContext context in _contexts) { context.Unload(); }
        _contexts.Clear();
    }

    private sealed class PluginLoadContext(string mainAssemblyPath)
        : AssemblyLoadContext($"LiraokeLanguage-{Guid.NewGuid():N}", isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);
        private readonly string _directory = Path.GetDirectoryName(mainAssemblyPath)!;
        private static readonly Assembly[] SharedContracts =
            [typeof(ILanguageAdaptor).Assembly, typeof(LyricLanguageSpan).Assembly];

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            Assembly? shared = SharedContracts.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
            if (shared is not null)
            {
                // Never load private copies of host contracts: that breaks type identity and can
                // make a seemingly correct plugin invisible to IsAssignableFrom.
                if (assemblyName.Version is { } requested && requested != shared.GetName().Version)
                {
                    throw new FileLoadException($"SDK assembly version mismatch: {assemblyName.Name}.");
                }
                return shared;
            }
            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is null && assemblyName.Name is { } name)
            {
                string adjacent = Path.Combine(_directory, name + ".dll");
                if (File.Exists(adjacent)) { path = adjacent; }
            }
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }
}
