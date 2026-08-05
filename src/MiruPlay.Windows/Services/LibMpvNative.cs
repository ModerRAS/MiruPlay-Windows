using System.Runtime.InteropServices;

namespace MiruPlay.Windows.Services;

internal enum LibMpvNodeKind
{
    None,
    String,
    Flag,
    Int64,
    Double,
    Array,
    Map,
}

internal sealed record LibMpvNodeValue(
    LibMpvNodeKind Kind,
    string? StringValue = null,
    bool? FlagValue = null,
    long? Int64Value = null,
    double? DoubleValue = null,
    IReadOnlyList<LibMpvNodeValue>? ArrayValue = null,
    IReadOnlyDictionary<string, LibMpvNodeValue>? MapValue = null);

internal static class LibMpvNodeDecoder
{
    private const int FormatNone = 0;
    private const int FormatString = 1;
    private const int FormatOsdString = 2;
    private const int FormatFlag = 3;
    private const int FormatInt64 = 4;
    private const int FormatDouble = 5;
    private const int FormatNode = 6;
    private const int FormatNodeArray = 7;
    private const int FormatNodeMap = 8;

    public static LibMpvNodeValue Decode(IntPtr nodePointer)
    {
        if (nodePointer == IntPtr.Zero) throw new ArgumentNullException(nameof(nodePointer));

        var raw = Marshal.ReadIntPtr(nodePointer);
        var format = Marshal.ReadInt32(nodePointer, 8);
        return format switch
        {
            FormatNone => new(LibMpvNodeKind.None),
            FormatString or FormatOsdString => new(
                LibMpvNodeKind.String,
                StringValue: raw == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(raw)),
            FormatFlag => new(LibMpvNodeKind.Flag, FlagValue: Marshal.ReadInt32(nodePointer) != 0),
            FormatInt64 => new(LibMpvNodeKind.Int64, Int64Value: raw.ToInt64()),
            FormatDouble => new(
                LibMpvNodeKind.Double,
                DoubleValue: BitConverter.Int64BitsToDouble(raw.ToInt64())),
            FormatNode or FormatNodeArray or FormatNodeMap => DecodeList(raw),
            _ => throw new InvalidOperationException($"Unsupported libmpv node format: {format}."),
        };
    }

    private static LibMpvNodeValue DecodeList(IntPtr listPointer)
    {
        if (listPointer == IntPtr.Zero) return new(LibMpvNodeKind.Array, ArrayValue: []);

        var count = Math.Max(0, Marshal.ReadInt32(listPointer));
        var valuesPointer = Marshal.ReadIntPtr(listPointer, 8);
        var keysPointer = Marshal.ReadIntPtr(listPointer, 16);
        if (keysPointer == IntPtr.Zero)
        {
            var values = Enumerable.Range(0, count)
                .Select(index => Decode(IntPtr.Add(valuesPointer, index * 16)))
                .ToArray();
            return new(LibMpvNodeKind.Array, ArrayValue: values);
        }

        var map = new Dictionary<string, LibMpvNodeValue>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var keyPointer = Marshal.ReadIntPtr(keysPointer, index * IntPtr.Size);
            var key = keyPointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(keyPointer) ?? string.Empty;
            map[key] = Decode(IntPtr.Add(valuesPointer, index * 16));
        }
        return new(LibMpvNodeKind.Map, MapValue: map);
    }
}

internal readonly record struct LibMpvEvent(
    int EventId,
    int Error,
    ulong ReplyUserdata,
    IntPtr Data);

internal interface ILibMpvClient : IDisposable
{
    int SetOptionString(string name, string value);
    int Initialize();
    int Command(IReadOnlyList<string> arguments);
    int SetPropertyString(string name, string value);
    string? GetPropertyString(string name);
    LibMpvNodeValue? GetPropertyNode(string name, uint format = 6);
    int ObserveProperty(ulong userdata, string name, uint format);
    LibMpvEvent WaitEvent(double timeoutSeconds);
}

internal sealed class LibMpvNativeApi : IDisposable
{
    private static readonly string[] RequiredExports =
    [
        "mpv_create",
        "mpv_set_option_string",
        "mpv_initialize",
        "mpv_command",
        "mpv_set_property_string",
        "mpv_get_property_string",
        "mpv_get_property",
        "mpv_set_property",
        "mpv_observe_property",
        "mpv_wait_event",
        "mpv_free",
        "mpv_free_node_contents",
        "mpv_terminate_destroy",
    ];

    private readonly IntPtr _library;
    private readonly MpvCreateDelegate _create;
    private readonly MpvSetOptionStringDelegate _setOptionString;
    private readonly MpvInitializeDelegate _initialize;
    private readonly MpvCommandDelegate _command;
    private readonly MpvSetPropertyStringDelegate _setPropertyString;
    private readonly MpvGetPropertyStringDelegate _getPropertyString;
    private readonly MpvGetPropertyDelegate _getProperty;
    private readonly MpvSetPropertyDelegate _setProperty;
    private readonly MpvObservePropertyDelegate _observeProperty;
    private readonly MpvWaitEventDelegate _waitEvent;
    private readonly MpvFreeDelegate _free;
    private readonly MpvFreeNodeContentsDelegate _freeNodeContents;
    private readonly MpvTerminateDestroyDelegate _terminateDestroy;
    private int _disposed;

    public LibMpvNativeApi(string libraryPath)
    {
        if (string.IsNullOrWhiteSpace(libraryPath)) throw new ArgumentException("A libmpv path is required.", nameof(libraryPath));
        _library = NativeLibrary.Load(Path.GetFullPath(libraryPath));
        try
        {
            _create = GetDelegate<MpvCreateDelegate>("mpv_create");
            _setOptionString = GetDelegate<MpvSetOptionStringDelegate>("mpv_set_option_string");
            _initialize = GetDelegate<MpvInitializeDelegate>("mpv_initialize");
            _command = GetDelegate<MpvCommandDelegate>("mpv_command");
            _setPropertyString = GetDelegate<MpvSetPropertyStringDelegate>("mpv_set_property_string");
            _getPropertyString = GetDelegate<MpvGetPropertyStringDelegate>("mpv_get_property_string");
            _getProperty = GetDelegate<MpvGetPropertyDelegate>("mpv_get_property");
            _setProperty = GetDelegate<MpvSetPropertyDelegate>("mpv_set_property");
            _observeProperty = GetDelegate<MpvObservePropertyDelegate>("mpv_observe_property");
            _waitEvent = GetDelegate<MpvWaitEventDelegate>("mpv_wait_event");
            _free = GetDelegate<MpvFreeDelegate>("mpv_free");
            _freeNodeContents = GetDelegate<MpvFreeNodeContentsDelegate>("mpv_free_node_contents");
            _terminateDestroy = GetDelegate<MpvTerminateDestroyDelegate>("mpv_terminate_destroy");
        }
        catch
        {
            NativeLibrary.Free(_library);
            throw;
        }
    }

    public static IReadOnlyList<string> RequiredExportNames => RequiredExports;

    public IntPtr Create() => _create();

    public int SetOptionString(IntPtr handle, string name, string value) =>
        WithUtf8(name, namePointer => WithUtf8(value, valuePointer => _setOptionString(handle, namePointer, valuePointer)));

    public int Initialize(IntPtr handle) => _initialize(handle);

    public int Command(IntPtr handle, IReadOnlyList<string> arguments)
    {
        using var command = new LibMpvArgumentArray(arguments);
        return _command(handle, command.Pointer);
    }

    public int SetPropertyString(IntPtr handle, string name, string value) =>
        WithUtf8(name, namePointer => WithUtf8(value, valuePointer => _setPropertyString(handle, namePointer, valuePointer)));

    public string? GetPropertyString(IntPtr handle, string name)
    {
        var result = WithUtf8(name, namePointer => _getPropertyString(handle, namePointer));
        if (result == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUTF8(result);
        }
        finally
        {
            _free(result);
        }
    }

    public LibMpvNodeValue? GetPropertyNode(IntPtr handle, string name, uint format = 6)
    {
        var node = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt64(node, 0);
            var error = WithUtf8(name, namePointer => _getProperty(handle, namePointer, format, node));
            return error < 0 ? null : LibMpvNodeDecoder.Decode(node);
        }
        finally
        {
            _freeNodeContents(node);
            Marshal.FreeHGlobal(node);
        }
    }

    public int SetPropertyString(IntPtr handle, string name, string value, uint format = 1) =>
        WithUtf8(name, namePointer => WithUtf8(value, valuePointer => _setProperty(handle, namePointer, format, valuePointer)));

    public int ObserveProperty(IntPtr handle, ulong replyUserdata, string name, uint format) =>
        WithUtf8(name, namePointer => _observeProperty(handle, replyUserdata, namePointer, format));

    public LibMpvEvent WaitEvent(IntPtr handle, double timeoutSeconds)
    {
        var pointer = _waitEvent(handle, timeoutSeconds);
        if (pointer == IntPtr.Zero) return new(0, 0, 0, IntPtr.Zero);
        var native = Marshal.PtrToStructure<LibMpvEventNative>(pointer);
        return new(native.EventId, native.Error, native.ReplyUserdata, native.Data);
    }

    public void TerminateDestroy(IntPtr handle)
    {
        if (handle != IntPtr.Zero) _terminateDestroy(handle);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        NativeLibrary.Free(_library);
    }

    private T GetDelegate<T>(string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_library, name, out var export))
            throw new EntryPointNotFoundException($"libmpv export not found: {name}");
        return Marshal.GetDelegateForFunctionPointer<T>(export);
    }

    private static T WithUtf8<T>(string value, Func<IntPtr, T> callback)
    {
        var pointer = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return callback(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LibMpvEventNative
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr MpvCreateDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvSetOptionStringDelegate(IntPtr handle, IntPtr name, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvInitializeDelegate(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvCommandDelegate(IntPtr handle, IntPtr arguments);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvSetPropertyStringDelegate(IntPtr handle, IntPtr name, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr MpvGetPropertyStringDelegate(IntPtr handle, IntPtr name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvGetPropertyDelegate(IntPtr handle, IntPtr name, uint format, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvSetPropertyDelegate(IntPtr handle, IntPtr name, uint format, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvObservePropertyDelegate(IntPtr handle, ulong replyUserdata, IntPtr name, uint format);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr MpvWaitEventDelegate(IntPtr handle, double timeoutSeconds);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MpvFreeDelegate(IntPtr pointer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MpvFreeNodeContentsDelegate(IntPtr node);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MpvTerminateDestroyDelegate(IntPtr handle);
}

internal sealed class LibMpvClient : ILibMpvClient
{
    private readonly LibMpvNativeApi _native;
    private IntPtr _handle;
    private int _disposed;

    private LibMpvClient(LibMpvNativeApi native, IntPtr handle)
    {
        _native = native;
        _handle = handle;
    }

    public IntPtr Handle => _handle == IntPtr.Zero ? throw new ObjectDisposedException(nameof(LibMpvClient)) : _handle;

    public static LibMpvClient Create(string libraryPath)
    {
        var native = new LibMpvNativeApi(libraryPath);
        var handle = native.Create();
        if (handle == IntPtr.Zero)
        {
            native.Dispose();
            throw new InvalidOperationException("libmpv failed to create a client handle.");
        }
        return new LibMpvClient(native, handle);
    }

    public int SetOptionString(string name, string value) => _native.SetOptionString(Handle, name, value);
    public int Initialize() => _native.Initialize(Handle);
    public int Command(IReadOnlyList<string> arguments) => _native.Command(Handle, arguments);
    public int SetPropertyString(string name, string value) => _native.SetPropertyString(Handle, name, value);
    public string? GetPropertyString(string name) => _native.GetPropertyString(Handle, name);
    public LibMpvNodeValue? GetPropertyNode(string name, uint format = 6) => _native.GetPropertyNode(Handle, name, format);
    public int ObserveProperty(ulong userdata, string name, uint format) => _native.ObserveProperty(Handle, userdata, name, format);
    public LibMpvEvent WaitEvent(double timeoutSeconds) => _native.WaitEvent(Handle, timeoutSeconds);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        _native.TerminateDestroy(handle);
        _native.Dispose();
    }
}
