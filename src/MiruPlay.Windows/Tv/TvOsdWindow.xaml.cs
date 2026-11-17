using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using MiruPlay.Windows.Services;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace MiruPlay.Windows.Tv;

/// <summary>
/// Layered, non-activating OSD window drawn above the libmpv child HWND. Keys
/// stay with the shell; this window only renders chrome (top bar, timeline,
/// transport, chips, drawer) and never takes focus.
/// </summary>
public sealed partial class TvOsdWindow : Window
{
    public const string BackHint = "Back 返回";

    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExTransparent = 0x00000020;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly Border[] _transport;
    private readonly TextBlock[] _transportText;
    private readonly Border[] _chips;
    private readonly TextBlock[] _chipText;
    private bool _visible;
    private bool _initialized;
    private string _drawerSignature = string.Empty;
    private TvOsdState? _lastState;

    public TvOsdWindow()
    {
        InitializeComponent();
        Cursor = System.Windows.Input.Cursors.None;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.None;
        InputMethod.SetIsInputMethodEnabled(this, false);
        _transport = [Transport0, Transport1, Transport2, Transport3, Transport4];
        _transportText = [TransportText0, TransportText1, TransportText2, TransportText3, TransportText4];
        _chips = [Chip0, Chip1, Chip2, Chip3];
        _chipText = [ChipText0, ChipText1, ChipText2, ChipText3];
        _transportText[0].Text = AppStrings.Get("tv.player.prev");
        _transportText[1].Text = AppStrings.Get("tv.player.seekBack");
        _transportText[2].Text = AppStrings.Get("tv.player.pause");
        _transportText[3].Text = AppStrings.Get("tv.player.seekForward");
        _transportText[4].Text = AppStrings.Get("tv.player.next");
        BackHintText.Text = AppStrings.Get("tv.player.back");
        SourceInitialized += OsdWindow_SourceInitialized;
        Loaded += (_, _) =>
        {
            _initialized = true;
            if (_lastState is not null) Present(_lastState);
        };
    }

    public void Present(TvOsdState state)
    {
        _lastState = state;
        if (!_initialized) return;
        if (!state.Visible)
        {
            SetPanelVisible(false);
            return;
        }

        SetPanelVisible(true);
        TitleText.Text = state.Title;
        SubtitleText.Text = state.Subtitle;
        MetaText.Text = state.MetaLine;
        ElapsedText.Text = TvPresentation.FormatTime(state.PositionMs);
        DurationText.Text = TvPresentation.FormatTime(state.DurationMs);
        TimelineBar.Maximum = Math.Max(1, state.DurationMs);
        TimelineBar.Value = Math.Clamp(state.PositionMs, 0, TimelineBar.Maximum);
        _transportText[2].Text = AppStrings.Get(state.IsPaused ? "tv.player.play" : "tv.player.pause");
        _chipText[0].Text = AppStrings.Get("tv.player.subtitleValue", state.SubtitleLabel);
        _chipText[1].Text = AppStrings.Get("tv.player.audioValue", state.AudioLabel);
        _chipText[2].Text = AppStrings.Get("tv.player.speedValue", state.SpeedLabel);
        _chipText[3].Text = AppStrings.Get("tv.player.info");
        StatusText.Text = state.Status ?? state.NextEpisode ?? string.Empty;
        StatusPill.Visibility = string.IsNullOrEmpty(StatusText.Text) ? Visibility.Collapsed : Visibility.Visible;

        foreach (var item in _transport) ResetFocus(item);
        foreach (var item in _chips) ResetFocus(item);
        if (state.FocusVisible)
        {
            if (state.FocusRow == 0 && state.FocusIndex >= 0 && state.FocusIndex < _transport.Length)
                ApplyFocus(_transport[state.FocusIndex]);
            else if (state.FocusRow == 1 && state.FocusIndex >= 0 && state.FocusIndex < _chips.Length)
                ApplyFocus(_chips[state.FocusIndex]);
        }

        BottomPanel.Visibility = state.Drawer is null ? Visibility.Visible : Visibility.Collapsed;
        PresentDrawer(state.Drawer);
    }

    public void SetVisible(bool visible)
    {
        if (!_initialized) return;
        if (visible && !_visible)
        {
            RootLayer.Visibility = Visibility.Visible;
            var animation = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(140) };
            RootLayer.BeginAnimation(OpacityProperty, animation);
        }
        else if (!visible && _visible)
        {
            var animation = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(140) };
            animation.Completed += (_, _) => RootLayer.Visibility = Visibility.Collapsed;
            RootLayer.BeginAnimation(OpacityProperty, animation);
        }
        _visible = visible;
    }

    public void SetError(string? message)
    {
        if (!_initialized) return;
        if (message is null)
        {
            ErrorLayer.Visibility = Visibility.Collapsed;
            return;
        }
        ErrorLayer.Visibility = Visibility.Visible;
        ErrorTitle.Text = AppStrings.Get("tv.player.error.title");
        ErrorMessage.Text = message;
        ErrorHint.Text = AppStrings.Get("tv.player.error.hint");
    }

    public void SyncGeometry(Window host)
    {
        var handle = new WindowInteropHelper(host).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return;
        var dpi = VisualTreeHelper.GetDpi(host);
        Left = rect.Left / dpi.DpiScaleX;
        Top = rect.Top / dpi.DpiScaleY;
        Width = Math.Max(1, (rect.Right - rect.Left) / dpi.DpiScaleX);
        Height = Math.Max(1, (rect.Bottom - rect.Top) / dpi.DpiScaleY);
        var own = new WindowInteropHelper(this).Handle;
        if (own != IntPtr.Zero)
            SetWindowPos(own, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private void OsdWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _ = ImmAssociateContext(handle, IntPtr.Zero);
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style |= WsExLayered | WsExToolWindow | WsExNoActivate | WsExTransparent;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
    }

    private void SetPanelVisible(bool visible)
    {
        if (!_visible && visible)
        {
            RootLayer.Visibility = Visibility.Visible;
            RootLayer.BeginAnimation(OpacityProperty, new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(150) });
        }
        else if (_visible && !visible)
        {
            var animation = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(150) };
            animation.Completed += (_, _) => RootLayer.Visibility = Visibility.Collapsed;
            RootLayer.BeginAnimation(OpacityProperty, animation);
        }
        _visible = visible;
    }

    private void PresentDrawer(TvOsdDrawer? drawer)
    {
        if (drawer is null)
        {
            DrawerPanel.Visibility = Visibility.Collapsed;
            _drawerSignature = string.Empty;
            return;
        }
        DrawerPanel.Visibility = Visibility.Visible;
        DrawerTitle.Text = drawer.Title;
        var signature = drawer.Title + "|" + string.Join(';', drawer.Items.Select((item, index) =>
            $"{index}:{item.Label}:{item.Value}:{item.IsSelected}:{index == drawer.FocusIndex}"));
        if (signature == _drawerSignature) return;
        _drawerSignature = signature;
        DrawerList.Children.Clear();
        var focusableIndex = 0;
        for (var index = 0; index < drawer.Items.Count; index++)
        {
            var item = drawer.Items[index];
            if (item.IsHeader)
            {
                DrawerList.Children.Add(new TextBlock
                {
                    Text = item.Label,
                    FontSize = 17,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("TvAccentBrush"),
                    Margin = new Thickness(0, index == 0 ? 0 : 20, 0, 8),
                });
                continue;
            }
            var focused = item.IsFocusable && focusableIndex == drawer.FocusIndex;
            if (item.IsFocusable) focusableIndex++;
            var row = new Border
            {
                Background = focused ? new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x36)) : Brushes.Transparent,
                BorderBrush = focused ? (Brush)FindResource("TvAccentBrush") : Brushes.Transparent,
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 2, 0, 2),
            };
            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = item.Label,
                FontSize = 19,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = item.IsSelected ? (Brush)FindResource("TvAccentBrush") : (Brush)FindResource("TvTextBrush"),
            };
            rowGrid.Children.Add(label);
            if (!string.IsNullOrEmpty(item.Value))
            {
                var value = new TextBlock
                {
                    Text = item.Value,
                    FontSize = 16,
                    Foreground = (Brush)FindResource("TvTextMutedBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(value, 1);
                rowGrid.Children.Add(value);
            }
            row.Child = rowGrid;
            DrawerList.Children.Add(row);
        }
    }

    private void ResetFocus(Border border)
    {
        border.Background = Brushes.Transparent;
        border.BorderBrush = Brushes.Transparent;
        border.RenderTransform = Transform.Identity;
        if (border.Child is TextBlock text) text.Foreground = (Brush)FindResource("TvTextSecondaryBrush");
    }

    private void ApplyFocus(Border border)
    {
        border.Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x36));
        border.BorderBrush = (Brush)FindResource("TvAccentBrush");
        border.RenderTransform = new ScaleTransform(1.05, 1.05);
        border.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.7 };
        if (border.Child is TextBlock text) text.Foreground = (Brush)FindResource("TvTextBrush");
    }

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hwnd, IntPtr himc);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, index) : new IntPtr(GetWindowLong32(hWnd, index));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, index, value) : new IntPtr(SetWindowLong32(hWnd, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int index, int value);
}