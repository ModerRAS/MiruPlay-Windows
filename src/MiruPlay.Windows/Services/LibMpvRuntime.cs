using System.Runtime.InteropServices;

namespace MiruPlay.Windows.Services;

internal static class LibMpvRuntime
{
    private const string LibraryName = "libmpv-2.dll";

    public static string? FindLibraryPath(
        string? configuredPath,
        IEnumerable<string>? searchDirectories,
        string? baseDirectory = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath)) candidates.Add(configuredPath.Trim());

        var root = baseDirectory ?? AppContext.BaseDirectory;
        candidates.Add(Path.Combine(root, "runtime", "libmpv", LibraryName));
        if (searchDirectories is not null)
        {
            candidates.AddRange(searchDirectories
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Select(directory => Path.Combine(directory.Trim(), LibraryName)));
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory.Trim('"'), LibraryName)));
        }

        return candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }
}

internal sealed class LibMpvArgumentArray : IDisposable
{
    private readonly IntPtr[] _arguments;
    private IntPtr _pointer;
    private int _disposed;

    public LibMpvArgumentArray(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        _arguments = new IntPtr[arguments.Count];
        _pointer = Marshal.AllocHGlobal((arguments.Count + 1) * IntPtr.Size);
        try
        {
            for (var index = 0; index < arguments.Count; index++)
            {
                ArgumentNullException.ThrowIfNull(arguments[index]);
                _arguments[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
                Marshal.WriteIntPtr(_pointer, index * IntPtr.Size, _arguments[index]);
            }
            Marshal.WriteIntPtr(_pointer, arguments.Count * IntPtr.Size, IntPtr.Zero);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Count => _arguments.Length;
    public IntPtr Pointer => _pointer == IntPtr.Zero ? throw new ObjectDisposedException(nameof(LibMpvArgumentArray)) : _pointer;

    public IReadOnlyList<string> ToArray()
    {
        var pointer = Pointer;
        return Enumerable.Range(0, Count)
            .Select(index => Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(pointer, index * IntPtr.Size)) ?? string.Empty)
            .ToArray();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var argument in _arguments)
        {
            if (argument != IntPtr.Zero) Marshal.FreeCoTaskMem(argument);
        }
        if (_pointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_pointer);
            _pointer = IntPtr.Zero;
        }
    }
}
