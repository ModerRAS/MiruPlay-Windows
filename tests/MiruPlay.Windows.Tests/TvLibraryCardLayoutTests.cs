using MiruPlay.Windows.Tv;

namespace MiruPlay.Windows.Tests;

public sealed class TvLibraryCardLayoutTests
{
    [Fact]
    public void KeepsFullSizeAt1080pGridHeight()
    {
        var size = TvLibraryCardLayout.CalculatePosterSize(938);
        Assert.Equal(219, size.Height);
        Assert.Equal(146, size.Width);
    }

    [Fact]
    public void FitsThreeRowsAt4K225PercentEffectiveHeight()
    {
        // 4K at 225% gives ~960 effective DIP; library grid area is ~830 DIP.
        const double available = 830;
        var size = TvLibraryCardLayout.CalculatePosterSize(available);
        Assert.InRange(size.Height, 140, 219);
        Assert.True(size.Height < 219);
        var rowHeight = available / TvLibraryCardLayout.Rows;
        Assert.True(size.Height + TvLibraryCardLayout.TitleReserve + TvLibraryCardLayout.VerticalCardMargin <= rowHeight + 0.01);
        Assert.True(size.Width > 0);
        Assert.Equal(Math.Round(size.Height * TvLibraryCardLayout.PosterAspect, MidpointRounding.AwayFromZero), size.Width);
    }

    [Fact]
    public void ClampsAtMinimumForTinyAvailableHeight()
    {
        var size = TvLibraryCardLayout.CalculatePosterSize(200);
        Assert.Equal(TvLibraryCardLayout.MinPosterHeight, size.Height);
    }

    [Fact]
    public void FallsBackToFullSizeWhenGridHasNotMeasured()
    {
        Assert.Equal((146, 219), TvLibraryCardLayout.CalculatePosterSize(0));
        Assert.Equal((146, 219), TvLibraryCardLayout.CalculatePosterSize(double.NaN));
    }
}
