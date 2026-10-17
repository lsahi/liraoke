using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace KaraokeFilmMaker.App.Controls;

/// <summary>Loads and releases files immediately; the WPF default URI converter keeps file handles open.</summary>
public sealed class ThumbnailImageConverter : IValueConverter
{
    private readonly Dictionary<string, BitmapSource> _images = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path)) { return null; }
        if (_images.TryGetValue(path, out var cached)) { return cached; }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 240; image.StreamSource = stream;
            image.EndInit(); image.Freeze();
            if (_images.Count >= 64) { _images.Clear(); }
            _images[path] = image;
            return image;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { return null; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
