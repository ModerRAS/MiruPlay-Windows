using System.Runtime.InteropServices;

namespace MiruPlay.Windows.Services;

/// <summary>
/// WinMM waveIn/waveOut audio I/O for room measurement (no new packages, no
/// COM interface IDs). Capture is 48 kHz mono 16-bit; rendering takes stereo
/// 16-bit at the device rate the caller specifies (48 kHz in practice).
/// All operations fail fast with messages; nothing is hidden.
/// </summary>
public sealed class WaveAudio
{
    public sealed record AudioDevice(int Id, string Name);

    private const uint CallbackFunction = 0x30000;
    private const int WimData = 0x3BF;
    private const int WomDone = 0x3BD;

    public static IReadOnlyList<AudioDevice> ListCaptureDevices()
    {
        var count = WaveInGetNumDevs();
        var devices = new List<AudioDevice>();
        for (var id = 0; id < count; id++)
        {
            var caps = new WaveInCaps();
            if (WaveInGetDevCaps((IntPtr)id, ref caps, (uint)Marshal.SizeOf<WaveInCaps>()) == 0 &&
                caps.Name is { } name)
                devices.Add(new AudioDevice(id, name));
        }
        return devices;
    }

    public static IReadOnlyList<AudioDevice> ListRenderDevices()
    {
        var count = WaveOutGetNumDevs();
        var devices = new List<AudioDevice>();
        for (var id = 0; id < count; id++)
        {
            var caps = new WaveOutCaps();
            if (WaveOutGetDevCaps((IntPtr)id, ref caps, (uint)Marshal.SizeOf<WaveOutCaps>()) == 0 &&
                caps.Name is { } name)
                devices.Add(new AudioDevice(id, name));
        }
        return devices;
    }

    /// <summary>Capture mono 16-bit PCM at <paramref name="sampleRateHz"/> for <paramref name="duration"/>.</summary>
    public static double[] Capture(int deviceId, int sampleRateHz, TimeSpan duration, CancellationToken cancellationToken)
    {
        var totalFrames = (int)(sampleRateHz * duration.TotalSeconds);
        if (totalFrames <= 0) throw new ArgumentOutOfRangeException(nameof(duration));
        var format = new WaveFormatEx
        {
            FormatTag = 1,
            Channels = 1,
            SamplesPerSec = (uint)sampleRateHz,
            AvgBytesPerSec = (uint)(sampleRateHz * 2),
            BlockAlign = 2,
            BitsPerSample = 16,
            Size = 0,
        };
        var buffer = new byte[totalFrames * 2];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        WaveCallback? callback = null;
        var completed = 0;
        var failed = 0;
        var header = new WaveHeader
        {
            Data = handle.AddrOfPinnedObject(),
            BufferLength = buffer.Length,
            BytesRecorded = 0,
        };
        var headerHandle = GCHandle.Alloc(header, GCHandleType.Pinned);
        var headerPointer = headerHandle.AddrOfPinnedObject();
        callback = (_, message, _, _, _) =>
        {
            if (message != WimData) return;
            try
            {
                var recorded = Marshal.PtrToStructure<WaveHeader>(headerPointer).BytesRecorded;
                Interlocked.Exchange(ref completed, recorded);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
            }
        };
        var callbackHandle = GCHandle.Alloc(callback);
        IntPtr waveIn = IntPtr.Zero;
        try
        {
            FailIfError(WaveInOpen(out waveIn, (IntPtr)deviceId, ref format,
                Marshal.GetFunctionPointerForDelegate(callback), IntPtr.Zero, CallbackFunction), AppStrings.Get("wave.openMic"));
            FailIfError(WaveInPrepareHeader(waveIn, headerHandle.AddrOfPinnedObject(), (uint)Marshal.SizeOf<WaveHeader>()), AppStrings.Get("wave.prepareCap"));
            FailIfError(WaveInAddBuffer(waveIn, headerHandle.AddrOfPinnedObject(), (uint)Marshal.SizeOf<WaveHeader>()), AppStrings.Get("wave.queueCap"));
            FailIfError(WaveInStart(waveIn), AppStrings.Get("wave.startCap"));
            var deadline = DateTimeOffset.UtcNow + duration + TimeSpan.FromSeconds(5);
            while (Volatile.Read(ref completed) < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    Interlocked.Exchange(ref failed, 1);
                    break;
                }
                Thread.Sleep(50);
            }
        }
        finally
        {
            if (waveIn != IntPtr.Zero)
            {
                _ = WaveInStop(waveIn);
                _ = WaveInReset(waveIn);
                _ = WaveInUnprepareHeader(waveIn, headerHandle.AddrOfPinnedObject(), (uint)Marshal.SizeOf<WaveHeader>());
                _ = WaveInClose(waveIn);
            }
            headerHandle.Free();
            callbackHandle.Free();
            handle.Free();
        }
        if (Volatile.Read(ref failed) != 0) throw new TimeoutException(AppStrings.Get("wave.capTimeout"));
        var recorded = Math.Min(Volatile.Read(ref completed) / 2, totalFrames);
        var samples = new double[recorded];
        for (var i = 0; i < recorded; i++)
            samples[i] = BitConverter.ToInt16(buffer, i * 2) / 32768.0;
        return samples;
    }

    /// <summary>Render interleaved stereo 16-bit PCM, blocking until done or cancelled.</summary>
    public static void Play(int deviceId, short[] stereoInterleaved, int sampleRateHz, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stereoInterleaved);
        if (stereoInterleaved.Length % 2 != 0) throw new ArgumentException(AppStrings.Get("wave.evenBuffer"));
        var format = new WaveFormatEx
        {
            FormatTag = 1,
            Channels = 2,
            SamplesPerSec = (uint)sampleRateHz,
            AvgBytesPerSec = (uint)(sampleRateHz * 4),
            BlockAlign = 4,
            BitsPerSample = 16,
            Size = 0,
        };
        var buffer = new byte[stereoInterleaved.Length * 2];
        Buffer.BlockCopy(stereoInterleaved, 0, buffer, 0, buffer.Length);
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        WaveCallback? callback = null;
        var finished = new ManualResetEventSlim(false);
        callback = (_, message, _, _, _) =>
        {
            if (message == WomDone) finished.Set();
        };
        var callbackHandle = GCHandle.Alloc(callback);
        var header = new WaveHeader
        {
            Data = handle.AddrOfPinnedObject(),
            BufferLength = buffer.Length,
        };
        var headerHandle = GCHandle.Alloc(header, GCHandleType.Pinned);
        IntPtr waveOut = IntPtr.Zero;
        try
        {
            FailIfError(WaveOutOpen(out waveOut, (IntPtr)deviceId, ref format,
                Marshal.GetFunctionPointerForDelegate(callback), IntPtr.Zero, CallbackFunction), AppStrings.Get("wave.openOutput"));
            FailIfError(WaveOutPrepareHeader(waveOut, headerHandle.AddrOfPinnedObject(), (uint)Marshal.SizeOf<WaveHeader>()), AppStrings.Get("wave.preparePlay"));
            FailIfError(WaveOutWrite(waveOut, headerHandle.AddrOfPinnedObject(), (uint)Marshal.SizeOf<WaveHeader>()), AppStrings.Get("wave.writePlay"));
            while (!finished.Wait(TimeSpan.FromMilliseconds(50), cancellationToken))
                cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            if (waveOut != IntPtr.Zero)
            {
                _ = WaveOutReset(waveOut);
                _ = WaveOutUnprepareHeader(waveOut, headerHandle.AddrOfPinnedObject(), (uint)Marshal.SizeOf<WaveHeader>());
                _ = WaveOutClose(waveOut);
            }
            headerHandle.Free();
            callbackHandle.Free();
            handle.Free();
            finished.Dispose();
        }
    }

    private static void FailIfError(int result, string message)
    {
        if (result != 0) throw new InvalidOperationException(AppStrings.Get("wave.mmresult", message, result));
    }

    private delegate void WaveCallback(IntPtr device, int message, IntPtr instance, IntPtr param1, IntPtr param2);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public int BufferLength;
        public int BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveInCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? Name;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? Name;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved;
        public uint Support;
    }

    [DllImport("winmm.dll", EntryPoint = "waveInGetNumDevs")]
    private static extern int WaveInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveInGetDevCaps")]
    private static extern int WaveInGetDevCaps(IntPtr deviceId, ref WaveInCaps caps, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveInOpen")]
    private static extern int WaveInOpen(out IntPtr handle, IntPtr deviceId, ref WaveFormatEx format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll", EntryPoint = "waveInPrepareHeader")]
    private static extern int WaveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveInAddBuffer")]
    private static extern int WaveInAddBuffer(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveInStart")]
    private static extern int WaveInStart(IntPtr handle);
    [DllImport("winmm.dll", EntryPoint = "waveInStop")]
    private static extern int WaveInStop(IntPtr handle);
    [DllImport("winmm.dll", EntryPoint = "waveInReset")]
    private static extern int WaveInReset(IntPtr handle);
    [DllImport("winmm.dll", EntryPoint = "waveInUnprepareHeader")]
    private static extern int WaveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveInClose")]
    private static extern int WaveInClose(IntPtr handle);
    [DllImport("winmm.dll", EntryPoint = "waveOutGetNumDevs")]
    private static extern int WaveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveOutGetDevCaps")]
    private static extern int WaveOutGetDevCaps(IntPtr deviceId, ref WaveOutCaps caps, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveOutOpen")]
    private static extern int WaveOutOpen(out IntPtr handle, IntPtr deviceId, ref WaveFormatEx format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll", EntryPoint = "waveOutPrepareHeader")]
    private static extern int WaveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveOutWrite")]
    private static extern int WaveOutWrite(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveOutReset")]
    private static extern int WaveOutReset(IntPtr handle);
    [DllImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader")]
    private static extern int WaveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveOutClose")]
    private static extern int WaveOutClose(IntPtr handle);
}
