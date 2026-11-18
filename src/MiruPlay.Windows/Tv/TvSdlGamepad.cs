using System.Runtime.InteropServices;

namespace MiruPlay.Windows.Tv;

/// <summary>
/// SDL3 gamepad backend. Loads SDL3.dll dynamically from runtime/sdl3 (then the
/// normal DLL search path), enumerates pads with SDL's built-in mapping database
/// (DualSense/DualShock/Switch Pro/8BitDo/Xbox, USB &amp; Bluetooth) and emits the
/// same <see cref="TvGamepadState"/> as the XInput source.
/// </summary>
public sealed class SdlGamepadSource : IXInputSource, IDisposable
{
    private const uint SdlInitGamepad = 0x00002000;

    // SDL3 positional gamepad buttons/axes.
    private const int ButtonSouth = 0;
    private const int ButtonEast = 1;
    private const int ButtonWest = 2;
    private const int ButtonNorth = 3;
    private const int ButtonBack = 4;
    private const int ButtonStart = 6;
    private const int ButtonLeftShoulder = 9;
    private const int ButtonRightShoulder = 10;
    private const int ButtonDpadUp = 11;
    private const int ButtonDpadDown = 12;
    private const int ButtonDpadLeft = 13;
    private const int ButtonDpadRight = 14;
    private const int AxisLeftX = 0;
    private const int AxisLeftY = 1;
    private const int AxisRightX = 2;
    private const int AxisTriggerLeft = 4;
    private const int AxisTriggerRight = 5;

    private static readonly object Sync = new();
    private static SdlApi? _api;
    private static bool _apiInitialized;
    private static int _references;

    private readonly Func<long> _clock;
    private IntPtr _gamepad;
    private long _nextScanAt;
    private bool _disposed;
    private string? _deviceName;

    public SdlGamepadSource(Func<long>? clock = null)
    {
        _clock = clock ?? (() => Environment.TickCount64);
        lock (Sync) _references++;
    }

    public TvGamepadFaceLayout FaceLayout { get; set; } = TvGamepadFaceLayout.Xbox;

    public string? DeviceName => _deviceName;

    public static bool IsAvailable => EnsureApi() is not null;

    public bool TryGetState(int userIndex, out TvGamepadState state)
    {
        state = default;
        if (userIndex != 0 || _disposed) return false;
        var api = EnsureApi();
        if (api is null) return false;
        var now = _clock();
        api.UpdateGamepads();
        if (_gamepad != IntPtr.Zero && !api.GamepadConnected(_gamepad))
        {
            api.CloseGamepad(_gamepad);
            _gamepad = IntPtr.Zero;
            _deviceName = null;
            _nextScanAt = now;
        }
        if (_gamepad == IntPtr.Zero && now >= _nextScanAt)
        {
            _nextScanAt = now + 1000; // hot-plug scan at most once per second
            TryOpen(api);
        }
        if (_gamepad == IntPtr.Zero)
        {
            state = new TvGamepadState(false, TvGamepadButtons.None);
            return false;
        }
        state = ReadState(api, _gamepad);
        return true;
    }

    private void TryOpen(SdlApi api)
    {
        foreach (var id in api.GetGamepads())
        {
            var pad = api.OpenGamepad(id);
            if (pad == IntPtr.Zero) continue;
            _gamepad = pad;
            _deviceName = api.GetGamepadName(pad);
            return;
        }
    }

    private TvGamepadState ReadState(SdlApi api, IntPtr pad)
    {
        var buttons = TvGamepadButtons.None;
        var south = api.GetButton(pad, ButtonSouth);
        var east = api.GetButton(pad, ButtonEast);
        if (FaceLayout == TvGamepadFaceLayout.Nintendo)
        {
            if (south) buttons |= TvGamepadButtons.B; // physical bottom is B
            if (east) buttons |= TvGamepadButtons.A;
        }
        else
        {
            if (south) buttons |= TvGamepadButtons.A;
            if (east) buttons |= TvGamepadButtons.B;
        }
        if (api.GetButton(pad, ButtonWest)) buttons |= TvGamepadButtons.X;
        if (api.GetButton(pad, ButtonNorth)) buttons |= TvGamepadButtons.Y;
        if (api.GetButton(pad, ButtonBack)) buttons |= TvGamepadButtons.B;
        if (api.GetButton(pad, ButtonStart)) buttons |= TvGamepadButtons.Start;
        if (api.GetButton(pad, ButtonLeftShoulder)) buttons |= TvGamepadButtons.LeftShoulder;
        if (api.GetButton(pad, ButtonRightShoulder)) buttons |= TvGamepadButtons.RightShoulder;
        if (api.GetButton(pad, ButtonDpadUp)) buttons |= TvGamepadButtons.DPadUp;
        if (api.GetButton(pad, ButtonDpadDown)) buttons |= TvGamepadButtons.DPadDown;
        if (api.GetButton(pad, ButtonDpadLeft)) buttons |= TvGamepadButtons.DPadLeft;
        if (api.GetButton(pad, ButtonDpadRight)) buttons |= TvGamepadButtons.DPadRight;

        var leftX = api.GetAxis(pad, AxisLeftX);
        var leftY = api.GetAxis(pad, AxisLeftY);
        return new TvGamepadState(
            Connected: true,
            Buttons: buttons,
            LeftTrigger: ToByte(api.GetAxis(pad, AxisTriggerLeft)),
            RightTrigger: ToByte(api.GetAxis(pad, AxisTriggerRight)),
            LeftThumbX: leftX,
            LeftThumbY: leftY == short.MinValue ? short.MaxValue : (short)-leftY, // SDL: +Y is down
            RightThumbX: api.GetAxis(pad, AxisRightX));
    }

    private static byte ToByte(short trigger) =>
        (byte)Math.Clamp((int)trigger * 255 / 32767, 0, 255);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (Sync)
        {
            if (_gamepad != IntPtr.Zero)
            {
                _api?.CloseGamepad(_gamepad);
                _gamepad = IntPtr.Zero;
            }
            _references--;
            if (_references > 0) return;
            _api?.Quit();
            _api = null;
            _apiInitialized = false;
        }
    }

    private static SdlApi? EnsureApi()
    {
        lock (Sync)
        {
            if (_apiInitialized) return _api;
            _apiInitialized = true;
            var api = SdlApi.Load();
            if (api is null)
            {
                TvGamepadDiagnostics.ReportSdl3DllMissing();
                return null;
            }
            if (!api.Init(SdlInitGamepad))
            {
                TvGamepadDiagnostics.ReportSdl3InitFailure(api.GetErrorText());
                api.Quit();
                return null;
            }
            _api = api;
            return api;
        }
    }

    private sealed class SdlApi
    {
        private readonly IntPtr _library;
        private readonly InitDelegate _init;
        private readonly Action _quit;
        private readonly GetGamepadsDelegate _getGamepads;
        private readonly FreeDelegate _free;
        private readonly OpenGamepadDelegate _openGamepad;
        private readonly CloseGamepadDelegate _closeGamepad;
        private readonly GamepadConnectedDelegate _gamepadConnected;
        private readonly UpdateGamepadsDelegate _updateGamepads;
        private readonly GetButtonDelegate _getButton;
        private readonly GetAxisDelegate _getAxis;
        private readonly GetNameDelegate _getGamepadName;
        private readonly GetErrorDelegate _getError;

        private SdlApi(
            IntPtr library,
            InitDelegate init,
            Action quit,
            GetGamepadsDelegate getGamepads,
            FreeDelegate free,
            OpenGamepadDelegate openGamepad,
            CloseGamepadDelegate closeGamepad,
            GamepadConnectedDelegate gamepadConnected,
            UpdateGamepadsDelegate updateGamepads,
            GetButtonDelegate getButton,
            GetAxisDelegate getAxis,
            GetNameDelegate getGamepadName,
            GetErrorDelegate getError)
        {
            _library = library;
            _init = init;
            _quit = quit;
            _getGamepads = getGamepads;
            _free = free;
            _openGamepad = openGamepad;
            _closeGamepad = closeGamepad;
            _gamepadConnected = gamepadConnected;
            _updateGamepads = updateGamepads;
            _getButton = getButton;
            _getAxis = getAxis;
            _getGamepadName = getGamepadName;
            _getError = getError;
        }

        public static SdlApi? Load()
        {
            foreach (var candidate in CandidatePaths())
            {
                if (!NativeLibrary.TryLoad(candidate, out var library)) continue;
                try
                {
                    return new SdlApi(
                        library,
                        Export<InitDelegate>(library, "SDL_Init", true)!,
                        Export<Action>(library, "SDL_Quit", true)!,
                        Export<GetGamepadsDelegate>(library, "SDL_GetGamepads", true)!,
                        Export<FreeDelegate>(library, "SDL_free", true)!,
                        Export<OpenGamepadDelegate>(library, "SDL_OpenGamepad", true)!,
                        Export<CloseGamepadDelegate>(library, "SDL_CloseGamepad", true)!,
                        Export<GamepadConnectedDelegate>(library, "SDL_GamepadConnected", true)!,
                        Export<UpdateGamepadsDelegate>(library, "SDL_UpdateGamepads", true)!,
                        Export<GetButtonDelegate>(library, "SDL_GetGamepadButton", true)!,
                        Export<GetAxisDelegate>(library, "SDL_GetGamepadAxis", true)!,
                        Export<GetNameDelegate>(library, "SDL_GetGamepadName", true)!,
                        Export<GetErrorDelegate>(library, "SDL_GetError", false)!);
                }
                catch (Exception error) when (error is EntryPointNotFoundException or ArgumentException)
                {
                    NativeLibrary.Free(library);
                }
            }
            return null;
        }

        private static IEnumerable<string> CandidatePaths()
        {
            yield return Path.Combine(AppContext.BaseDirectory, "runtime", "sdl3", "SDL3.dll");
            yield return "SDL3.dll";
        }

        private static T? Export<T>(IntPtr library, string name, bool required) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(library, name, out var address))
            {
                if (required) throw new EntryPointNotFoundException(name);
                return null;
            }
            return System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        public bool Init(uint flags) => _init(flags);
        public void Quit() => _quit();
        public void UpdateGamepads() => _updateGamepads();

        public int[] GetGamepads()
        {
            var array = _getGamepads(out var count);
            if (array == IntPtr.Zero || count <= 0) return [];
            try
            {
                var ids = new int[count];
                Marshal.Copy(array, ids, 0, count);
                return ids;
            }
            finally
            {
                _free(array);
            }
        }

        public IntPtr OpenGamepad(int id) => _openGamepad(id);
        public void CloseGamepad(IntPtr pad) => _closeGamepad(pad);
        public bool GamepadConnected(IntPtr pad) => _gamepadConnected(pad);
        public bool GetButton(IntPtr pad, int button) => _getButton(pad, button);
        public short GetAxis(IntPtr pad, int axis) => _getAxis(pad, axis);

        public string? GetGamepadName(IntPtr pad)
        {
            var pointer = _getGamepadName(pad);
            return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);
        }

        public string? GetErrorText()
        {
            if (_getError is null) return null;
            var pointer = _getError();
            return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.U1)]
        private delegate bool InitDelegate(uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr GetGamepadsDelegate(out int count);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void FreeDelegate(IntPtr memory);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr OpenGamepadDelegate(int joystickId);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void CloseGamepadDelegate(IntPtr gamepad);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.U1)]
        private delegate bool GamepadConnectedDelegate(IntPtr gamepad);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void UpdateGamepadsDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.U1)]
        private delegate bool GetButtonDelegate(IntPtr gamepad, int button);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate short GetAxisDelegate(IntPtr gamepad, int axis);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr GetNameDelegate(IntPtr gamepad);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr GetErrorDelegate();
    }
}

/// <summary>
/// Backend policy: SDL3 is preferred (any HID pad, SDL mapping database); XInput
/// is the zero-dependency fallback for Xbox pads. Only one backend reports a
/// connected pad at a time, so a controller is never processed twice.
/// </summary>
public sealed class AutoGamepadSource : IXInputSource, IDisposable
{
    private readonly SdlGamepadSource _sdl;
    private readonly XInputSource _xinput = new();

    public AutoGamepadSource(Func<long>? clock = null) => _sdl = new SdlGamepadSource(clock);

    public TvGamepadFaceLayout FaceLayout
    {
        get => _sdl.FaceLayout;
        set => _sdl.FaceLayout = value;
    }

    public static bool Sdl3Available => SdlGamepadSource.IsAvailable;

    public string ActiveBackend { get; private set; } = "none";

    private bool _lastConnected;
    private string? _lastDeviceName;

    public bool TryGetState(int userIndex, out TvGamepadState state)
    {
        if (_sdl.TryGetState(userIndex, out state) && state.Connected)
        {
            ActiveBackend = "SDL3";
            _lastConnected = true;
            _lastDeviceName = _sdl.DeviceName;
            return true;
        }
        if (_xinput.TryGetState(userIndex, out state) && state.Connected)
        {
            ActiveBackend = "XInput";
            _lastConnected = true;
            _lastDeviceName = null;
            return true;
        }
        ActiveBackend = Sdl3Available ? "SDL3" : "XInput";
        _lastConnected = false;
        _lastDeviceName = null;
        state = new TvGamepadState(false, TvGamepadButtons.None);
        return false;
    }

    public TvGamepadStatus GetStatus()
    {
        var sdlAvailable = Sdl3Available;
        return new TvGamepadStatus(
            Demo: false,
            Sdl3DllMissing: TvGamepadDiagnostics.Sdl3DllMissing,
            Sdl3InitFailed: !sdlAvailable && !TvGamepadDiagnostics.Sdl3DllMissing,
            Sdl3Error: TvGamepadDiagnostics.Sdl3InitError,
            Connected: _lastConnected,
            ActiveBackend: ActiveBackend,
            DeviceName: _lastDeviceName);
    }

    public void Dispose() => _sdl.Dispose();
}
