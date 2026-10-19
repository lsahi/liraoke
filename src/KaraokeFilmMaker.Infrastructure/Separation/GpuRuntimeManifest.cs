using System.Text.Json;

namespace KaraokeFilmMaker.Infrastructure.Separation;

public sealed record GpuRuntimeManifest(string Id, string TorchVersion, string WheelUrl, string WheelSha256,
    long WheelBytes, long TemporarySpaceBytes, string LicenseUrl)
{
    public static GpuRuntimeManifest Load()
    {
        using var stream = typeof(GpuRuntimeManifest).Assembly.GetManifestResourceStream(
            "KaraokeFilmMaker.Infrastructure.Separation.Runtime.gpu-runtime.json")
            ?? throw new InvalidDataException("缺少 GPU 组件清单。");
        var value = JsonSerializer.Deserialize<GpuRuntimeManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        if (!IsOfficialUrl(new Uri(value.WheelUrl)) || value.WheelSha256.Length != 64
            || !value.WheelSha256.All(Uri.IsHexDigit) || value.WheelBytes <= 0 || Path.GetFileName(value.Id) != value.Id)
            throw new InvalidDataException("GPU 组件清单无效。");
        return value;
    }

    public static bool IsOfficialUrl(Uri uri) => uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && uri.Host is "download.pytorch.org" or "download-r2.pytorch.org";
}
