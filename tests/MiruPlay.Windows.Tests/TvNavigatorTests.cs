using MiruPlay.Windows.Tv;

namespace MiruPlay.Windows.Tests;

public sealed class TvNavigatorTests
{
    [Fact]
    public void StartsAtHomeWithoutBackTarget()
    {
        var navigator = new TvNavigator();
        Assert.Equal(TvScreenId.Home, navigator.Current);
        Assert.False(navigator.CanGoBack);
        Assert.False(navigator.Pop());
    }

    [Fact]
    public void PushAndPopWalkTheScreenStack()
    {
        var navigator = new TvNavigator();
        navigator.Push(TvScreenId.Library);
        navigator.Push(TvScreenId.Detail);
        Assert.Equal(TvScreenId.Detail, navigator.Current);
        Assert.True(navigator.CanGoBack);
        Assert.True(navigator.Pop());
        Assert.Equal(TvScreenId.Library, navigator.Current);
        Assert.True(navigator.Pop());
        Assert.Equal(TvScreenId.Home, navigator.Current);
        Assert.False(navigator.Pop());
    }

    [Fact]
    public void ResetToKeepsHomeRootAndPushesDestination()
    {
        var navigator = new TvNavigator();
        navigator.Push(TvScreenId.Library);
        navigator.Push(TvScreenId.Detail);

        navigator.ResetTo(TvScreenId.Settings);

        Assert.Equal([TvScreenId.Home, TvScreenId.Settings], navigator.Stack.Select(entry => entry.Screen).ToArray());
        Assert.Equal(TvScreenId.Settings, navigator.Current);
        Assert.True(navigator.Pop());
        Assert.Equal(TvScreenId.Home, navigator.Current);
    }

    [Fact]
    public void ResetToHomeClearsEverything()
    {
        var navigator = new TvNavigator();
        navigator.ResetTo(TvScreenId.Search);
        navigator.Push(TvScreenId.Player);

        navigator.ResetTo(TvScreenId.Home);

        Assert.Single(navigator.Stack);
        Assert.Equal(TvScreenId.Home, navigator.Current);
    }

    [Fact]
    public void FocusMemoryIsRememberedPerScreen()
    {
        var navigator = new TvNavigator();
        navigator.RememberFocus(TvScreenId.Library, "grid:7");
        navigator.RememberFocus(TvScreenId.Home, "row:continue:2");

        Assert.Equal("grid:7", navigator.RecallFocus(TvScreenId.Library));
        Assert.Equal("row:continue:2", navigator.RecallFocus(TvScreenId.Home));
        Assert.Null(navigator.RecallFocus(TvScreenId.Settings));

        navigator.RememberFocus(TvScreenId.Library, null);
        Assert.Null(navigator.RecallFocus(TvScreenId.Library));

        navigator.ClearFocusMemory(TvScreenId.Home);
        Assert.Null(navigator.RecallFocus(TvScreenId.Home));
    }

    [Fact]
    public void ChangedEventReportsPushAndPop()
    {
        var navigator = new TvNavigator();
        var events = new List<TvNavigationChangedEventArgs>();
        navigator.Changed += (_, args) => events.Add(args);

        navigator.Push(TvScreenId.Library);
        navigator.Pop();

        Assert.Equal(2, events.Count);
        Assert.False(events[0].IsPop);
        Assert.Equal(TvScreenId.Home, events[0].Previous);
        Assert.Equal(TvScreenId.Library, events[0].Current);
        Assert.True(events[1].IsPop);
        Assert.Equal(TvScreenId.Library, events[1].Previous);
        Assert.Equal(TvScreenId.Home, events[1].Current);
    }
}
