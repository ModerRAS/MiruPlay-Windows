using MiruPlay.Windows.Tv;

namespace MiruPlay.Windows.Tests;

public sealed class TvZoneTests
{
    // TvZone only counts items for movement; WPF controls are not created.
    private static TvZone Zone(TvZoneKind kind, int count, int columns = 1) =>
        new("z", kind, new System.Windows.FrameworkElement[count], columns);

    [Fact]
    public void HorizontalZoneClampsAtBothEnds()
    {
        var zone = Zone(TvZoneKind.Horizontal, 3);
        Assert.True(zone.TryMove(TvInputAction.Right, 0, out var next));
        Assert.Equal(1, next);
        Assert.True(zone.TryMove(TvInputAction.Right, 1, out next));
        Assert.Equal(2, next);
        Assert.False(zone.TryMove(TvInputAction.Right, 2, out _));
        Assert.False(zone.TryMove(TvInputAction.Left, 0, out _));
        Assert.False(zone.TryMove(TvInputAction.Up, 1, out _));
    }

    [Fact]
    public void GridZoneMovesRowMajorAndStopsAtEdges()
    {
        var zone = Zone(TvZoneKind.Grid, 6, columns: 3);
        Assert.True(zone.TryMove(TvInputAction.Right, 0, out var next));
        Assert.Equal(1, next);
        Assert.True(zone.TryMove(TvInputAction.Right, 2, out next));
        Assert.Equal(3, next); // wraps to the next row
        Assert.True(zone.TryMove(TvInputAction.Down, 0, out next));
        Assert.Equal(3, next);
        Assert.True(zone.TryMove(TvInputAction.Up, 4, out next));
        Assert.Equal(1, next);
        Assert.False(zone.TryMove(TvInputAction.Up, 1, out _));
        Assert.False(zone.TryMove(TvInputAction.Down, 4, out _));
        Assert.False(zone.TryMove(TvInputAction.Left, 3, out _));
    }

    [Fact]
    public void GridZoneDownOnLastRowClampsToLastItem()
    {
        var zone = Zone(TvZoneKind.Grid, 5, columns: 3); // last row has two items
        Assert.True(zone.TryMove(TvInputAction.Down, 1, out var next));
        Assert.Equal(4, next);
        Assert.False(zone.TryMove(TvInputAction.Down, 3, out _));
    }

    [Fact]
    public void EmptyZoneNeverMoves()
    {
        var zone = Zone(TvZoneKind.Horizontal, 0);
        Assert.False(zone.TryMove(TvInputAction.Right, 0, out _));
    }
}
