using System.Security.Cryptography;
using System.Text.Json;

namespace KaraokeFilmMaker.Infrastructure.Alignment;

public sealed record AlignmentDownloadSpec(string Sha256, long Bytes);
public sealed record AlignmentCacheImportResult(int Imported, int Existing, int Rejected);

/// <summary>Content-addressed cache: names and external directory structure are never trusted.</summary>
public sealed class AlignmentDownloadCache(string directory, IReadOnlyList<AlignmentDownloadSpec> catalog)
{
    public static IReadOnlyList<AlignmentDownloadSpec> ReadCatalog(string manifest)
    {
        using var json = JsonDocument.Parse(manifest);
        var result = new List<AlignmentDownloadSpec>();
        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("sha256", out var hash) && element.TryGetProperty("bytes", out var size))
                    result.Add(new(hash.GetString()!, size.GetInt64()));
                foreach (var item in element.EnumerateObject()) Visit(item.Value);
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Visit(item);
        }
        Visit(json.RootElement);
        return result.Distinct().ToArray();
    }

    public static async Task<bool> VerifiedAsync(string path, AlignmentDownloadSpec spec, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != spec.Bytes) return false;
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(spec.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AlignmentCacheImportResult> ImportAsync(IEnumerable<string> files, IProgress<string>? progress, CancellationToken token)
    {
        int imported = 0, existing = 0, rejected = 0;
        foreach (string path in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var candidates = catalog.Where(s => s.Bytes == new FileInfo(path).Length).ToArray();
            if (candidates.Length == 0) { rejected++; continue; }
            progress?.Report("校验本地文件：" + Path.GetFileName(path));
            string digest;
            await using (var input = File.OpenRead(path)) digest = Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant();
            var spec = candidates.FirstOrDefault(s => s.Sha256.Equals(digest, StringComparison.OrdinalIgnoreCase));
            if (spec is null) { rejected++; continue; }
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, digest);
            if (await VerifiedAsync(destination, spec, token)) { existing++; continue; }
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".import";
            try
            {
                await using (var input = File.OpenRead(path))
                await using (var output = File.Create(temporary)) await input.CopyToAsync(output, token);
                // Detect source changes during copying; only validated bytes are promoted.
                if (!await VerifiedAsync(temporary, spec, token)) throw new IOException("本地文件在复制期间变化，未导入缓存。");
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, true); imported++;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        progress?.Report($"缓存导入完成：新增 {imported}，已存在 {existing}，不匹配 {rejected}。仅接收当前版本清单中的文件。");
        return new(imported, existing, rejected);
    }
}
