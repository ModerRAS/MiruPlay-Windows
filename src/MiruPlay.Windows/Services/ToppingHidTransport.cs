using System.Runtime.InteropServices;

namespace MiruPlay.Windows.Services;

public sealed record ToppingHidDevice(string DevicePath, string Product, ToppingProtocol.DeviceSpec Spec);

public interface IToppingHidTransport
{
    IReadOnlyList<ToppingHidDevice> ListDevices();
    void SendFrame(ToppingHidDevice device, byte[] wireBytes);
}

/// <summary>
/// Native Windows HID transport (setupapi + hid.dll, no new packages).
/// Enumeration only matches Thesycon-VID devices whose USB product string
/// identifies a known Topping model — the shared PID alone never qualifies.
/// </summary>
public sealed class WindowsHidTransport : IToppingHidTransport
{
    private const int DigcfPresent = 0x00000002;
    private const int DigcfDeviceinterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    public IReadOnlyList<ToppingHidDevice> ListDevices()
    {
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceinterface);
        if (set == new IntPtr(-1)) return [];
        try
        {
            var found = new List<ToppingHidDevice>();
            var interfaceData = new SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint index = 0; ; index++)
            {
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                    break;
                var path = GetDevicePath(set, ref interfaceData);
                if (path is null) continue;
                var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                    IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (handle == new IntPtr(-1)) continue;
                try
                {
                    var attributes = new HiddAttributes { Size = (uint)Marshal.SizeOf<HiddAttributes>() };
                    if (!HidD_GetAttributes(handle, ref attributes)) continue;
                    if (attributes.VendorId != ToppingProtocol.VendorId || attributes.ProductId != ToppingProtocol.ProductId)
                        continue;
                    var product = GetProductString(handle);
                    var spec = ToppingProtocol.MatchProduct(product);
                    if (spec is null) continue;
                    found.Add(new ToppingHidDevice(path, product ?? string.Empty, spec));
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            return found;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    public void SendFrame(ToppingHidDevice device, byte[] wireBytes)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(wireBytes);
        var handle = CreateFile(device.DevicePath, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1))
            throw new InvalidOperationException("无法打开 Topping DAC 的 HID 接口。");
        try
        {
            // Interrupt-OUT first (exact wire bytes, toppingctl parity), then SET_REPORT.
            if (WriteFile(handle, wireBytes, (uint)wireBytes.Length, out var written, IntPtr.Zero) &&
                written == wireBytes.Length)
                return;
            if (HidD_SetOutputReport(handle, wireBytes, (uint)wireBytes.Length))
                return;
            throw new InvalidOperationException("USB 写入失败（帧未送达）。");
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? GetDevicePath(IntPtr set, ref SpDeviceInterfaceData interfaceData)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
        if (requiredSize == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            // cbSize is the offset of the DevicePath string: 8 on x64, 4 + char size on x86.
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, buffer, requiredSize, out _, IntPtr.Zero))
                return null;
            return Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? GetProductString(IntPtr handle)
    {
        const int capacity = 128;
        var buffer = Marshal.AllocHGlobal(capacity * 2);
        try
        {
            return HidD_GetProductString(handle, buffer, (uint)(capacity * 2))
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("hid.dll", SetLastError = true)]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetAttributes(IntPtr hidDeviceObject, ref HiddAttributes attributes);

    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetProductString(IntPtr hidDeviceObject, IntPtr buffer, uint bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_SetOutputReport(IntPtr hidDeviceObject, byte[] reportBuffer, uint reportBufferLength);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
        out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(
        IntPtr file, byte[] buffer, uint numberOfBytesToWrite,
        out uint numberOfBytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public uint Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }
}
