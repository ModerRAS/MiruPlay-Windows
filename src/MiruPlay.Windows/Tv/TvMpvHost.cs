using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tv;

public sealed class TvRawKeyEventArgs(Key key) : EventArgs
{
    public Key Key { get; } = key;
}

/// <summary>
/// Native child HWND for libmpv's --wid embedding. Also forwards raw keyboard
/// messages from the child window (if it ever receives focus) to the shell and
/// suppresses the system arrow cursor over the video surface.
/// </summary>
public sealed class TvMpvHost : HwndHost
{
    private const int SsNotify = 0x0100;
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int SwpNoActivate = 0x0010;
    private const int SwpNoZOrder = 0x0004;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmSetCursor = 0x0020;

    public IntPtr NativeHandle { get; private set; }

    public event EventHandler<TvRawKeyEventArgs>? RawKeyDown;

    public event EventHandler<TvRawKeyEventArgs>? RawKeyUp;

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmKeyDown:
            case WmSysKeyDown:
                RawKeyDown?.Invoke(this, new TvRawKeyEventArgs(KeyInterop.KeyFromVirtualKey((int)wParam.ToInt64())));
                break;
            case WmKeyUp:
            case WmSysKeyUp:
                RawKeyUp?.Invoke(this, new TvRawKeyEventArgs(KeyInterop.KeyFromVirtualKey((int)wParam.ToInt64())));
                break;
            case WmSetCursor:
                NativeMethods.SetCursor(IntPtr.Zero);
                handled = true;
                return IntPtr.Zero;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        NativeHandle = NativeMethods.CreateWindowEx(
            0,
            "STATIC",
            string.Empty,
            WsChild | WsVisible | SsNotify,
            0,
            0,
            Math.Max(1, (int)ActualWidth),
            Math.Max(1, (int)ActualHeight),
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (NativeHandle == IntPtr.Zero)
            throw new InvalidOperationException(AppStrings.Get("mpv.hwndFailed", Marshal.GetLastWin32Error()));
        return new HandleRef(this, NativeHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero) NativeMethods.DestroyWindow(hwnd.Handle);
        NativeHandle = IntPtr.Zero;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (NativeHandle == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(
            NativeHandle,
            IntPtr.Zero,
            0,
            0,
            Math.Max(1, (int)ActualWidth),
            Math.Max(1, (int)ActualHeight),
            SwpNoActivate | SwpNoZOrder);
    }

    public void ValidateForMpv()
    {
        if (NativeHandle == IntPtr.Zero || !NativeMethods.IsWindow(NativeHandle))
            throw new InvalidOperationException(AppStrings.Get("mpv.noHwnd"));
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            int exStyle,
            string className,
            string windowName,
            int style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr handle,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            int flags);

        [DllImport("user32.dll")]
        public static extern IntPtr SetCursor(IntPtr handle);
    }
}
