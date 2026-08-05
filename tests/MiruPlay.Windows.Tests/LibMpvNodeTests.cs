using System.Runtime.InteropServices;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class LibMpvNodeTests : IDisposable
{
    private readonly List<IntPtr> _allocations = [];
    private readonly List<IntPtr> _coTaskAllocations = [];

    [Fact]
    public void DecoderReadsMpvNodeMapsAndNestedScalarValues()
    {
        var values = Marshal.AllocHGlobal(16 * 2);
        var keys = Marshal.AllocHGlobal(IntPtr.Size * 2);
        var list = Marshal.AllocHGlobal(24);
        var root = Marshal.AllocHGlobal(16);
        Track(values, keys, list, root);

        var language = Marshal.StringToCoTaskMemUTF8("jpn");
        TrackCoTask(language);
        WriteNode(values, 0, 1, language);
        WriteNode(values, 16, 3, new IntPtr(1));

        var languageKey = Marshal.StringToCoTaskMemUTF8("lang");
        var selectedKey = Marshal.StringToCoTaskMemUTF8("selected");
        TrackCoTask(languageKey, selectedKey);
        Marshal.WriteIntPtr(keys, 0, languageKey);
        Marshal.WriteIntPtr(keys, IntPtr.Size, selectedKey);

        Marshal.WriteInt32(list, 2);
        Marshal.WriteIntPtr(list, 8, values);
        Marshal.WriteIntPtr(list, 16, keys);
        WriteNode(root, 0, 8, list);

        var decoded = LibMpvNodeDecoder.Decode(root);

        Assert.Equal(LibMpvNodeKind.Map, decoded.Kind);
        Assert.Equal("jpn", decoded.MapValue!["lang"].StringValue);
        Assert.True(decoded.MapValue["selected"].FlagValue);
    }

    private void Track(params IntPtr[] pointers) => _allocations.AddRange(pointers);

    private void TrackCoTask(params IntPtr[] pointers) => _coTaskAllocations.AddRange(pointers);

    private static void WriteNode(IntPtr basePointer, int offset, int format, IntPtr data)
    {
        Marshal.WriteIntPtr(basePointer, offset, data);
        Marshal.WriteInt32(basePointer, offset + 8, format);
        Marshal.WriteInt32(basePointer, offset + 12, 0);
    }

    public void Dispose()
    {
        foreach (var pointer in _allocations)
        {
            if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer);
        }
        foreach (var pointer in _coTaskAllocations)
        {
            if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
        }
    }
}
