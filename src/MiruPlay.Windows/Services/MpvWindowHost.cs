using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MiruPlay.Windows.Services;

/// <summary>Provides a native child HWND for mpv's --wid embedding mode.</summary>
public sealed class MpvWindowHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int SwpNoActivate = 0x0010;
    private const int SwpNoZOrder = 0x0004;

    public new IntPtr Handle { get; private set; }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        Handle = NativeMethods.CreateWindowEx(
            0,
            "STATIC",
            string.Empty,
            WsChild | WsVisible,
            0,
            0,
            Math.Max(1, (int)ActualWidth),
            Math.Max(1, (int)ActualHeight),
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException($"无法创建 mpv 视频窗口，Win32 错误 {Marshal.GetLastWin32Error()}。");
        return new HandleRef(this, Handle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero) NativeMethods.DestroyWindow(hwnd.Handle);
        Handle = IntPtr.Zero;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (Handle == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(
            Handle,
            IntPtr.Zero,
            0,
            0,
            Math.Max(1, (int)ActualWidth),
            Math.Max(1, (int)ActualHeight),
            SwpNoActivate | SwpNoZOrder);
    }

    public void ValidateForMpv()
    {
        if (Handle == IntPtr.Zero || !NativeMethods.IsWindow(Handle))
            throw new InvalidOperationException("mpv 视频窗口尚未创建。");
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
    }
}
