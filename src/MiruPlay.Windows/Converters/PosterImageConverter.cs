using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace MiruPlay.Windows.Converters;

public sealed class PosterImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path ||
            Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ||
            !File.Exists(path)) return null;

        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = input;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
