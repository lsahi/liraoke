using System.Globalization;
using System.Text.Json;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Media;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.Infrastructure.Media;

public sealed record MediaDetails(ProjectDuration Duration, int? Width, int? Height, double? FrameRate, bool HasAudio);

public sealed class FfprobeMediaReader(MediaToolchain tools) : IMediaMetadataReader, IMediaTool
{
    public Task<MediaDetails> ReadDetailsAsync(string path, CancellationToken cancellationToken = default) =>
        ReadDetailsCoreAsync(path, requireStillImage: false, cancellationToken);

    private async Task<MediaDetails> ReadDetailsCoreAsync(string path, bool requireStillImage, CancellationToken cancellationToken)
    {
        string json = await MediaProcess.RunAsync(tools.FfprobePath,
            ["-v", "error", "-show_format", "-show_streams", "-of", "json", path], cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement format = root.GetProperty("format");
        JsonElement[] streams = root.GetProperty("streams").EnumerateArray().ToArray();
        JsonElement video = streams.FirstOrDefault(item => item.GetProperty("codec_type").GetString() == "video");
        bool hasVideo = video.ValueKind != JsonValueKind.Undefined;
        string? formatName = format.TryGetProperty("format_name", out JsonElement name) ? name.GetString() : null;
        string? imageCodec = hasVideo && video.TryGetProperty("codec_name", out JsonElement codec) ? codec.GetString() : null;
        // JPEG files with EXIF/progressive encoding may be detected as image2 instead of jpeg_pipe.
        // Only accept supported image codecs, never a video container merely renamed to .jpg.
        bool isStillImage = hasVideo && streams.Length == 1 && (formatName switch
        {
            "png_pipe" => imageCodec == "png",
            "jpeg_pipe" => imageCodec == "mjpeg",
            "bmp_pipe" => imageCodec == "bmp",
            "image2" => imageCodec is "png" or "mjpeg" or "bmp",
            _ => false,
        });
        if (requireStillImage && !isStillImage)
        {
            throw new InvalidDataException("仅支持静态 PNG、JPEG 或 BMP 图片，请先将其他格式转换为静态图片。");
        }
        if (requireStillImage)
        {
            // Validate dimensions before decoding. Read the exact file, not an image2 filename sequence,
            // and stop after two packets so a concatenated JPEG stream cannot masquerade as a still.
            ImageMediaLimits.ValidateDimensions(video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32());
            await ValidateSingleImageFrameAsync(path, imageCodec!, cancellationToken).ConfigureAwait(false);
        }
        double seconds = 0;
        if (!isStillImage && (!format.TryGetProperty("duration", out JsonElement duration)
            || !double.TryParse(duration.GetString(), CultureInfo.InvariantCulture, out seconds)
            || !double.IsFinite(seconds) || seconds <= 0))
        {
            throw new InvalidDataException("无法读取素材的有效时长。");
        }
        double? rate = null;
        if (hasVideo && video.TryGetProperty("avg_frame_rate", out JsonElement frameRate))
        {
            string[] parts = frameRate.GetString()!.Split('/');
            if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out double numerator)
                && double.TryParse(parts[1], CultureInfo.InvariantCulture, out double denominator) && denominator != 0)
            {
                rate = numerator / denominator;
            }
        }

        return new MediaDetails(ProjectDuration.FromSeconds(seconds),
            hasVideo ? video.GetProperty("width").GetInt32() : null,
            hasVideo ? video.GetProperty("height").GetInt32() : null, rate,
            streams.Any(item => item.GetProperty("codec_type").GetString() == "audio"));
    }

    private async Task ValidateSingleImageFrameAsync(string path, string codec, CancellationToken cancellationToken)
    {
        string json = await MediaProcess.RunAsync(tools.FfprobePath,
            ["-v", "error", "-f", "image2pipe", "-c:v", codec,
                "-max_pixels", ImageMediaLimits.MaximumPixels.ToString(CultureInfo.InvariantCulture),
                "-read_intervals", "%+#2", "-count_frames",
                "-show_entries", "stream=nb_read_frames", "-of", "json", path], cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement[] streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        if (streams.Length != 1 || !streams[0].TryGetProperty("nb_read_frames", out JsonElement count)
            || count.GetString() != "1")
        {
            throw new InvalidDataException("所选文件不是单帧静态图片，请先将动画或视频转换为静态 PNG、JPEG 或 BMP 图片。");
        }
    }

    public async Task<MediaProbeResult> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        MediaDetails details = await ReadDetailsAsync(filePath, cancellationToken).ConfigureAwait(false);
        return new MediaProbeResult(details.Duration, details.Width, details.Height, details.FrameRate, details.HasAudio);
    }

    public async Task<MediaProbeResult> ReadAsync(string filePath, MediaKind kind, CancellationToken cancellationToken = default)
    {
        if (kind == MediaKind.Image && !ImagePreviewCache.IsSupportedPath(filePath))
        {
            throw new InvalidDataException("仅支持静态 PNG、JPEG 或 BMP 图片。");
        }
        MediaDetails details = await ReadDetailsCoreAsync(filePath, kind == MediaKind.Image, cancellationToken).ConfigureAwait(false);
        if (kind != MediaKind.Image && details.Duration.Microseconds <= 0)
        {
            throw new InvalidDataException("所选文件没有有效的音频或视频时长；静态图片请使用“导入图片”。");
        }
        return new MediaProbeResult(details.Duration, details.Width, details.Height, details.FrameRate, details.HasAudio);
    }
}
