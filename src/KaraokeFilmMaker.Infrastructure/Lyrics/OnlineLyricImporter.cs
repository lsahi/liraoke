using System.Text;
using System.Text.Json;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Infrastructure.Lyrics;

public sealed record LyricImportSelection(string Text, bool UseLrc, string Provider, string Title, string Artist, string? SourceUrl);

public sealed class OnlineLyricImporter
{
    public async Task<LyricDocument> ImportAsync(string workspacePath, LyricImportSelection selection, LyricLanguage language,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selection.Text) || selection.Text.Length > 256_000)
        { throw new InvalidDataException("请提供非空且不超过 256000 字符的歌词文本。"); }
        if (!string.IsNullOrWhiteSpace(selection.SourceUrl) && (!Uri.TryCreate(selection.SourceUrl, UriKind.Absolute, out var source)
            || source.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(source.UserInfo)))
        { throw new InvalidDataException("来源地址必须是无账号凭据的 HTTPS 网页地址。"); }
        LyricDocument parsed = new LyricImportService().Parse(selection.Text, selection.UseLrc ? ".lrc" : ".txt", language);
        if (parsed.Lines.Count == 0) { throw new InvalidDataException("没有可导入的歌词行；请检查文本或关闭 LRC 时间选项。"); }
        string directory = Path.Combine(Path.GetFullPath(workspacePath), "lyrics");
        Directory.CreateDirectory(directory);
        string name = $"online-{Guid.NewGuid():N}";
        string textFile = Path.Combine(directory, name + (selection.UseLrc ? ".lrc" : ".txt"));
        string metadata = Path.Combine(directory, name + ".source.json");
        string temporary = textFile + ".tmp";
        var provenance = new LyricSource(selection.Provider, selection.Title, selection.Artist, selection.SourceUrl,
            Path.GetRelativePath(workspacePath, textFile).Replace('\\', '/'), DateTimeOffset.UtcNow);
        try
        {
            await File.WriteAllTextAsync(temporary, selection.Text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(provenance, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, textFile);
            return parsed with { Source = provenance };
        }
        catch
        {
            foreach (string owned in new[] { temporary, textFile, metadata }) { if (File.Exists(owned)) { File.Delete(owned); } }
            throw;
        }
    }
}
