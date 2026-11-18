using System.Runtime.InteropServices;

namespace MiruPlay.Windows.Tv;

/// <summary>Physical buttons exposed by an XInput-compatible (Xbox layout) pad.</summary>
[Flags]
public enum TvGamepadButtons : ushort
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

/// <summary>Snapshot of one XInput controller. Deadzone interpretation is done by the service.</summary>
public readonly record struct TvGamepadState(
    bool Connected,
    TvGamepadButtons Buttons,
    byte LeftTrigger = 0,
    byte RightTrigger = 0,
    short LeftThumbX = 0,
    short LeftThumbY = 0,
    short RightThumbX = 0,
    short RightThumbY = 0,
    uint PacketNumber = 0);

/// <summary>Injectable XInput device source (fake sources drive unit tests and --tv-pad-demo).</summary>
public interface IXInputSource
{
    bool TryGetState(int userIndex, out TvGamepadState state);
}

/// <summary>
/// Real XInput source. Loads xinput1_4.dll and falls back to the legacy
/// xinput9_1_0.dll (shipped since Vista) so older Windows setups also work.
/// </summary>
public sealed class XInputSource : IXInputSource
{
    private const int MaxControllers = 4;
    private static readonly object Sync = new();
    private static XInputGetStateDelegate? _getState;
    private static bool _initialized;

    public bool TryGetState(int userIndex, out TvGamepadState state)
    {
        state = default;
        if (userIndex is < 0 or >= MaxControllers) return false;
        EnsureLoaded();
        var getState = _getState;
        if (getState is null) return false;
        var result = getState((uint)userIndex, out var nativeState);
        if (result != 0) return false; // ERROR_DEVICE_NOT_CONNECTED or any other failure
        state = new TvGamepadState(
            Connected: true,
            Buttons: (TvGamepadButtons)nativeState.Gamepad.Buttons,
            LeftTrigger: nativeState.Gamepad.LeftTrigger,
            RightTrigger: nativeState.Gamepad.RightTrigger,
            LeftThumbX: nativeState.Gamepad.LeftThumbX,
            LeftThumbY: nativeState.Gamepad.LeftThumbY,
            RightThumbX: nativeState.Gamepad.RightThumbX,
            RightThumbY: nativeState.Gamepad.RightThumbY,
            PacketNumber: nativeState.PacketNumber);
        return true;
    }

    private static void EnsureLoaded()
    {
        if (_initialized) return;
        lock (Sync)
        {
            if (_initialized) return;
            _initialized = true;
            foreach (var library in new[] { "xinput1_4.dll", "xinput9_1_0.dll" })
            {
                if (!NativeLibrary.TryLoad(library, out var handle)) continue;
                if (!NativeLibrary.TryGetExport(handle, "XInputGetState", out var export)) continue;
                _getState = Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(export);
                return;
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint XInputGetStateDelegate(uint userIndex, out NativeXInputState state);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeXInputState
    {
        public uint PacketNumber;
        public NativeXInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeXInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftThumbX;
        public short LeftThumbY;
        public short RightThumbX;
        public short RightThumbY;
    }
}

/// <summary>Test/demo source whose state is set explicitly by the caller.</summary>
public sealed class FakeXInputSource : IXInputSource
{
    public TvGamepadState State { get; set; }

    public int PollCount { get; private set; }

    public bool TryGetState(int userIndex, out TvGamepadState state)
    {
        PollCount++;
        state = userIndex == 0 ? State : default;
        return userIndex == 0 && State.Connected;
    }
}
