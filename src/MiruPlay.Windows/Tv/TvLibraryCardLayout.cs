namespace MiruPlay.Windows.Tv;

/// <summary>
/// Responsive poster size for the fixed 6x3 media-library wall. The TV shell can
/// run at effective DIP heights well below 1080 (e.g. 4K at 225% scaling), so the
/// poster height must leave room for the title/subtitle chrome and row margins
/// inside each UniformGrid cell instead of assuming 1080p.
/// </summary>
internal static class TvLibraryCardLayout
{
    internal const int Columns = 6;
    internal const int Rows = 3;
    internal const double PosterAspect = 146.0 / 219.0;
    internal const double TitleReserve = 74;
    internal const double VerticalCardMargin = 16;
    internal const double MinPosterHeight = 140;
    internal const double MaxPosterHeight = 219;

    internal static (double Width, double Height) CalculatePosterSize(double availableHeight)
    {
        if (availableHeight <= 0 || double.IsNaN(availableHeight) || double.IsInfinity(availableHeight))
            return (146, MaxPosterHeight);
        var rowHeight = availableHeight / Rows;
        var posterHeight = Math.Clamp(
            Math.Floor(rowHeight - TitleReserve - VerticalCardMargin),
            MinPosterHeight,
            MaxPosterHeight);
        var posterWidth = Math.Round(posterHeight * PosterAspect, MidpointRounding.AwayFromZero);
        return (Math.Max(1, posterWidth), posterHeight);
    }
}
