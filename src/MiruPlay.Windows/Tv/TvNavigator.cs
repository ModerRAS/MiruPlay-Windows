namespace MiruPlay.Windows.Tv;

public enum TvScreenId
{
    Home,
    Library,
    Search,
    Settings,
    Detail,
    Player,
}

public sealed record TvNavigationEntry(TvScreenId Screen);

public sealed class TvNavigationChangedEventArgs(TvScreenId previous, TvScreenId current, bool isPop) : EventArgs
{
    public TvScreenId Previous { get; } = previous;
    public TvScreenId Current { get; } = current;
    public bool IsPop { get; } = isPop;
}

/// <summary>
/// TV screen stack: Home is the root, top-level destinations are pushed from
/// Home, Back pops. Focus memory is stored per screen id so returning from a
/// detail page lands on the poster that opened it.
/// </summary>
public sealed class TvNavigator
{
    private readonly List<TvNavigationEntry> _stack = [new(TvScreenId.Home)];
    private readonly Dictionary<TvScreenId, string> _focusMemory = [];

    public event EventHandler<TvNavigationChangedEventArgs>? Changed;

    public IReadOnlyList<TvNavigationEntry> Stack => _stack;

    public TvScreenId Current => _stack[^1].Screen;

    public TvScreenId Previous => _stack.Count > 1 ? _stack[^2].Screen : _stack[^1].Screen;

    public bool CanGoBack => _stack.Count > 1;

    public TvScreenId Push(TvScreenId screen)
    {
        var previous = Current;
        _stack.Add(new TvNavigationEntry(screen));
        Changed?.Invoke(this, new TvNavigationChangedEventArgs(previous, screen, isPop: false));
        return screen;
    }

    public bool Pop()
    {
        if (!CanGoBack) return false;
        var previous = Current;
        _stack.RemoveAt(_stack.Count - 1);
        Changed?.Invoke(this, new TvNavigationChangedEventArgs(previous, Current, isPop: true));
        return true;
    }

    /// <summary>Select a top-level destination: stack becomes [Home] plus the destination when it is not Home.</summary>
    public void ResetTo(TvScreenId screen)
    {
        var previous = Current;
        _stack.Clear();
        _stack.Add(new(TvScreenId.Home));
        if (screen != TvScreenId.Home) _stack.Add(new(screen));
        Changed?.Invoke(this, new TvNavigationChangedEventArgs(previous, Current, isPop: false));
    }

    public void RememberFocus(TvScreenId screen, string? focusKey)
    {
        if (string.IsNullOrEmpty(focusKey))
        {
            _focusMemory.Remove(screen);
            return;
        }
        _focusMemory[screen] = focusKey;
    }

    public string? RecallFocus(TvScreenId screen) =>
        _focusMemory.TryGetValue(screen, out var key) ? key : null;

    public void ClearFocusMemory(TvScreenId screen) => _focusMemory.Remove(screen);
}
