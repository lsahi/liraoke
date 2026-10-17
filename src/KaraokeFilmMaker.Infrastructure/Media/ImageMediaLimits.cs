namespace KaraokeFilmMaker.Infrastructure.Media;

public static class ImageMediaLimits
{
    public const int MaximumDimension = 16_384;
    public const long MaximumPixels = 120_000_000;

    public static void ValidateDimensions(int? width, int? height)
    {
        if (width is null or <= 0 || height is null or <= 0)
        {
            throw new InvalidDataException("无法读取图片的有效尺寸，请重新保存为 PNG、JPEG 或 BMP。");
        }
        if (width > MaximumDimension || height > MaximumDimension || (long)width.Value * height.Value > MaximumPixels)
        {
            throw new InvalidDataException("图片过大：单边不能超过 16,384 像素，总像素不能超过 1.2 亿。请先缩小图片后再导入。");
        }
    }
}
