using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tv;

public enum TvZoneKind
{
    Horizontal,
    Vertical,
    Grid,
}

/// <summary>A row/column/grid of focusable controls inside a screen.</summary>
public sealed class TvZone
{
    public TvZone(
        string id,
        TvZoneKind kind,
        IReadOnlyList<FrameworkElement> items,
        int columns = 1,
        Action<int>? onFocusChanged = null,
        Func<int, TvInputAction, bool>? onEdge = null)
    {
        Id = id;
        Kind = kind;
        Items = items;
        Columns = Math.Max(1, columns);
        OnFocusChanged = onFocusChanged;
        OnEdge = onEdge;
    }

    public string Id { get; }
    public TvZoneKind Kind { get; }
    public IReadOnlyList<FrameworkElement> Items { get; }
    public int Columns { get; }
    public Action<int>? OnFocusChanged { get; }
    public Func<int, TvInputAction, bool>? OnEdge { get; }

    public int IndexOf(FrameworkElement element)
    {
        for (var index = 0; index < Items.Count; index++)
            if (ReferenceEquals(Items[index], element)) return index;
        return -1;
    }

    public bool TryMove(TvInputAction direction, int current, out int next)
    {
        next = current;
        if (Items.Count == 0) return false;
        var currentIndex = Math.Clamp(current, 0, Items.Count - 1);
        switch (Kind)
        {
            case TvZoneKind.Horizontal:
                if (direction == TvInputAction.Left && currentIndex > 0) { next = currentIndex - 1; return true; }
                if (direction == TvInputAction.Right && currentIndex < Items.Count - 1) { next = currentIndex + 1; return true; }
                return false;
            case TvZoneKind.Vertical:
                if (direction == TvInputAction.Up && currentIndex > 0) { next = currentIndex - 1; return true; }
                if (direction == TvInputAction.Down && currentIndex < Items.Count - 1) { next = currentIndex + 1; return true; }
                return false;
            case TvZoneKind.Grid:
                var row = currentIndex / Columns;
                var column = currentIndex % Columns;
                var maxRow = (Items.Count - 1) / Columns;
                switch (direction)
                {
                    case TvInputAction.Left when column > 0: next = currentIndex - 1; return true;
                    case TvInputAction.Right when currentIndex < Items.Count - 1: next = currentIndex + 1; return true;
                    case TvInputAction.Up when row > 0: next = currentIndex - Columns; return true;
                    case TvInputAction.Down when row < maxRow:
                        next = Math.Min(currentIndex + Columns, Items.Count - 1);
                        return true;
                    default: return false;
                }
            default:
                return false;
        }
    }
}

/// <summary>
/// Base class for TV screens: an explicit, deterministic focus model (zones and
/// indices) instead of WPF's spatial navigation, so remote behaviour is stable
/// and testable. WPF keyboard focus is still moved onto the active control so
/// focus ring triggers and automation work.
/// </summary>
public abstract class TvScreenBase : UserControl
{
    private readonly List<TvZone> _zones = [];
    private int _focusRequest;

    public TvShellWindow? Shell { get; internal set; }

    public abstract TvScreenId Id { get; }

    public virtual string ScreenTitle => AppStrings.Get("tv.shell.title");

    public virtual string HintText => string.Empty;

    public TvZone? ActiveZone { get; private set; }

    public int ActiveIndex { get; private set; }

    public IReadOnlyList<TvZone> Zones => _zones;

    public bool IsActive => ActiveZone is not null;

    public virtual void OnActivated(string? focusKey)
    {
        if (focusKey is not null && RestoreFocus(focusKey)) return;
        FocusInitial();
    }

    public virtual string? OnDeactivating() =>
        ActiveZone is null ? null : $"{ActiveZone.Id}:{ActiveIndex}";

    public virtual bool HandleAction(TvInputAction action)
    {
        switch (action)
        {
            case TvInputAction.Left:
            case TvInputAction.Right:
            case TvInputAction.Up:
            case TvInputAction.Down:
                return Move(action);
            case TvInputAction.Select:
                ActivateCurrent();
                return true;
            default:
                return false;
        }
    }

    public virtual bool HandleMenu() => false;

    /// <summary>Screen-local Back handling; returning false lets the shell pop the navigator.</summary>
    public virtual bool HandleBack() => false;

    public virtual bool FocusInitial() => FocusFirstZone();

    public virtual bool FocusFromNav() => FocusInitial();

    public void ClearActiveFocus()
    {
        if (ActiveZone is { } zone && ActiveIndex >= 0 && ActiveIndex < zone.Items.Count)
            zone.Items[ActiveIndex].ClearValue(UIElement.IsKeyboardFocusWithinProperty);
        ActiveZone = null;
        ActiveIndex = 0;
    }

    protected void RegisterZone(TvZone zone) => _zones.Add(zone);

    protected void RegisterZones(params TvZone[] zones)
    {
        foreach (var zone in zones) RegisterZone(zone);
    }

    protected void UnregisterZone(TvZone zone) => _zones.Remove(zone);

    protected void ClearZones()
    {
        _zones.Clear();
        ActiveZone = null;
        ActiveIndex = 0;
    }

    protected int ZoneIndexOf(TvZone? zone) => zone is null ? -1 : _zones.IndexOf(zone);

    protected bool FocusFirstZone()
    {
        foreach (var zone in _zones)
        {
            if (zone.Items.Count == 0) continue;
            FocusZone(zone, 0);
            return true;
        }
        return false;
    }

    public void FocusZone(TvZone zone, int index)
    {
        if (zone.Items.Count == 0) return;
        var next = Math.Clamp(index, 0, zone.Items.Count - 1);
        ActiveZone = zone;
        ActiveIndex = next;
        var request = ++_focusRequest;
        ApplyFocus(zone, next, request, attempt: 0);
    }

    /// <summary>
    /// Re-applies keyboard focus for the current logical focus target. Screens
    /// are focused immediately when they are pushed, before WPF has completed
    /// layout, so <see cref="UIElement.Focus()"/> can return false and no focus
    /// ring is drawn until the next key press. The shell calls this again after
    /// the new screen finished loading.
    /// </summary>
    public void RefocusActive()
    {
        if (ActiveZone is { } zone && zone.Items.Count > 0) FocusZone(zone, ActiveIndex);
        else FocusInitial();
    }

    private void ApplyFocus(TvZone zone, int index, int request, int attempt)
    {
        if (request != _focusRequest || !ReferenceEquals(ActiveZone, zone) || ActiveIndex != index) return;
        if (index < 0 || index >= zone.Items.Count) return;
        var item = zone.Items[index];
        item.BringIntoView();
        if (item.Focus())
        {
            zone.OnFocusChanged?.Invoke(index);
            return;
        }
        if (attempt >= 2)
        {
            // Give up after layout/input retries; the next remote key will focus it.
            zone.OnFocusChanged?.Invoke(index);
            return;
        }
        var priority = attempt == 0 ? DispatcherPriority.Loaded : DispatcherPriority.Input;
        Dispatcher.BeginInvoke(priority, new Action(() => ApplyFocus(zone, index, request, attempt + 1)));
    }

    protected bool Move(TvInputAction direction)
    {
        if (ActiveZone is not { } zone) return FocusInitial();
        if (zone.TryMove(direction, ActiveIndex, out var next))
        {
            FocusZone(zone, next);
            return true;
        }
        if (zone.OnEdge?.Invoke(ActiveIndex, direction) == true) return true;
        return OnMoveOutOfZone(direction);
    }

    /// <summary>Called when the active zone cannot move further; override to hand focus to another zone.</summary>
    protected virtual bool OnMoveOutOfZone(TvInputAction direction) => false;

    protected void ActivateCurrent()
    {
        if (ActiveZone is not { } zone || ActiveIndex < 0 || ActiveIndex >= zone.Items.Count) return;
        var item = zone.Items[ActiveIndex];
        if (item is ButtonBase button)
        {
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return;
        }
        item.RaiseEvent(new RoutedEventArgs(UIElement.PreviewMouseLeftButtonUpEvent));
    }

    protected bool RestoreFocus(string focusKey)
    {
        var separator = focusKey.LastIndexOf(':');
        if (separator <= 0) return false;
        var zoneId = focusKey[..separator];
        if (!int.TryParse(focusKey[(separator + 1)..], out var index)) return false;
        var zone = _zones.FirstOrDefault(candidate => candidate.Id == zoneId);
        if (zone is null || zone.Items.Count == 0) return false;
        FocusZone(zone, index);
        return true;
    }
}