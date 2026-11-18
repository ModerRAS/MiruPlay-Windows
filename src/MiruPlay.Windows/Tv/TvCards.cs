using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MiruPlay.Windows.Tv;

/// <summary>Poster / wide-still tile used by home rows and the library wall.</summary>
public sealed class TvPosterCard : Button
{
    public static readonly DependencyProperty PosterSourceProperty = DependencyProperty.Register(
        nameof(PosterSource), typeof(ImageSource), typeof(TvPosterCard), new PropertyMetadata(null));

    public static readonly DependencyProperty TitleTextProperty = DependencyProperty.Register(
        nameof(TitleText), typeof(string), typeof(TvPosterCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleTextProperty = DependencyProperty.Register(
        nameof(SubtitleText), typeof(string), typeof(TvPosterCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ProgressPercentProperty = DependencyProperty.Register(
        nameof(ProgressPercent), typeof(double), typeof(TvPosterCard), new PropertyMetadata(0d));

    public static readonly DependencyProperty ShowProgressProperty = DependencyProperty.Register(
        nameof(ShowProgress), typeof(bool), typeof(TvPosterCard), new PropertyMetadata(false));

    public static readonly DependencyProperty BadgeTextProperty = DependencyProperty.Register(
        nameof(BadgeText), typeof(string), typeof(TvPosterCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PosterWidthProperty = DependencyProperty.Register(
        nameof(PosterWidth), typeof(double), typeof(TvPosterCard), new PropertyMetadata(180d));

    public static readonly DependencyProperty PosterHeightProperty = DependencyProperty.Register(
        nameof(PosterHeight), typeof(double), typeof(TvPosterCard), new PropertyMetadata(270d));

    public static readonly DependencyProperty TitleFontSizeProperty = DependencyProperty.Register(
        nameof(TitleFontSize), typeof(double), typeof(TvPosterCard), new PropertyMetadata(19d));

    public TvPosterCard()
    {
        Focusable = true;
        Cursor = System.Windows.Input.Cursors.None;
    }

    public ImageSource? PosterSource
    {
        get => (ImageSource?)GetValue(PosterSourceProperty);
        set => SetValue(PosterSourceProperty, value);
    }

    public string TitleText
    {
        get => (string)GetValue(TitleTextProperty);
        set => SetValue(TitleTextProperty, value);
    }

    public string SubtitleText
    {
        get => (string)GetValue(SubtitleTextProperty);
        set => SetValue(SubtitleTextProperty, value);
    }

    public double ProgressPercent
    {
        get => (double)GetValue(ProgressPercentProperty);
        set => SetValue(ProgressPercentProperty, value);
    }

    public bool ShowProgress
    {
        get => (bool)GetValue(ShowProgressProperty);
        set => SetValue(ShowProgressProperty, value);
    }

    public string BadgeText
    {
        get => (string)GetValue(BadgeTextProperty);
        set => SetValue(BadgeTextProperty, value);
    }

    public double PosterWidth
    {
        get => (double)GetValue(PosterWidthProperty);
        set => SetValue(PosterWidthProperty, value);
    }

    public double PosterHeight
    {
        get => (double)GetValue(PosterHeightProperty);
        set => SetValue(PosterHeightProperty, value);
    }

    public double TitleFontSize
    {
        get => (double)GetValue(TitleFontSizeProperty);
        set => SetValue(TitleFontSizeProperty, value);
    }
}

/// <summary>Text tile used for category/genre collection cards.</summary>
public sealed class TvTextCard : Button
{
    public static readonly DependencyProperty TitleTextProperty = DependencyProperty.Register(
        nameof(TitleText), typeof(string), typeof(TvTextCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleTextProperty = DependencyProperty.Register(
        nameof(SubtitleText), typeof(string), typeof(TvTextCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty AccentTextProperty = DependencyProperty.Register(
        nameof(AccentText), typeof(string), typeof(TvTextCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(TvTextCard), new PropertyMetadata(260d));

    public static readonly DependencyProperty CardHeightProperty = DependencyProperty.Register(
        nameof(CardHeight), typeof(double), typeof(TvTextCard), new PropertyMetadata(150d));

    public TvTextCard()
    {
        Focusable = true;
        Cursor = System.Windows.Input.Cursors.None;
    }

    public string TitleText
    {
        get => (string)GetValue(TitleTextProperty);
        set => SetValue(TitleTextProperty, value);
    }

    public string SubtitleText
    {
        get => (string)GetValue(SubtitleTextProperty);
        set => SetValue(SubtitleTextProperty, value);
    }

    public string AccentText
    {
        get => (string)GetValue(AccentTextProperty);
        set => SetValue(AccentTextProperty, value);
    }

    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    public double CardHeight
    {
        get => (double)GetValue(CardHeightProperty);
        set => SetValue(CardHeightProperty, value);
    }
}

/// <summary>Episode tile for the detail page episode row.</summary>
public sealed class TvEpisodeCard : Button
{
    public static readonly DependencyProperty NumberTextProperty = DependencyProperty.Register(
        nameof(NumberText), typeof(string), typeof(TvEpisodeCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TitleTextProperty = DependencyProperty.Register(
        nameof(TitleText), typeof(string), typeof(TvEpisodeCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleTextProperty = DependencyProperty.Register(
        nameof(SubtitleText), typeof(string), typeof(TvEpisodeCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ProgressPercentProperty = DependencyProperty.Register(
        nameof(ProgressPercent), typeof(double), typeof(TvEpisodeCard), new PropertyMetadata(0d));

    public static readonly DependencyProperty ShowProgressProperty = DependencyProperty.Register(
        nameof(ShowProgress), typeof(bool), typeof(TvEpisodeCard), new PropertyMetadata(false));

    public static readonly DependencyProperty IsWatchedProperty = DependencyProperty.Register(
        nameof(IsWatched), typeof(bool), typeof(TvEpisodeCard), new PropertyMetadata(false));

    public static readonly DependencyProperty IsCurrentProperty = DependencyProperty.Register(
        nameof(IsCurrent), typeof(bool), typeof(TvEpisodeCard), new PropertyMetadata(false));

    public TvEpisodeCard()
    {
        Focusable = true;
        Cursor = System.Windows.Input.Cursors.None;
    }

    public string NumberText
    {
        get => (string)GetValue(NumberTextProperty);
        set => SetValue(NumberTextProperty, value);
    }

    public string TitleText
    {
        get => (string)GetValue(TitleTextProperty);
        set => SetValue(TitleTextProperty, value);
    }

    public string SubtitleText
    {
        get => (string)GetValue(SubtitleTextProperty);
        set => SetValue(SubtitleTextProperty, value);
    }

    public double ProgressPercent
    {
        get => (double)GetValue(ProgressPercentProperty);
        set => SetValue(ProgressPercentProperty, value);
    }

    public bool ShowProgress
    {
        get => (bool)GetValue(ShowProgressProperty);
        set => SetValue(ShowProgressProperty, value);
    }

    public bool IsWatched
    {
        get => (bool)GetValue(IsWatchedProperty);
        set => SetValue(IsWatchedProperty, value);
    }

    public bool IsCurrent
    {
        get => (bool)GetValue(IsCurrentProperty);
        set => SetValue(IsCurrentProperty, value);
    }
}

public static class TvTheme
{
    private static readonly Uri ThemeSource = new(
        "pack://application:,,,/MiruPlay;component/Tv/TvTheme.xaml", UriKind.Absolute);

    public static void Apply(FrameworkElement element)
    {
        element.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = ThemeSource });
    }

    public static Style Style(FrameworkElement element, string key) => (Style)element.FindResource(key);

    public static object Resource(FrameworkElement element, string key) => element.FindResource(key);
}

public static class TvImages
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
        }
        ImageSource? image = null;
        try
        {
            if (File.Exists(path))
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 480;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or UriFormatException)
        {
            image = null;
        }
        lock (Cache)
        {
            Cache[path] = image;
        }
        return image;
    }
}