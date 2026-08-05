using System.Runtime.InteropServices;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class LibMpvNativeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-libmpv-{Guid.NewGuid():N}");

    public LibMpvNativeTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ConfiguredLibraryPathWinsWhenItExists()
    {
        var configuredPath = Path.Combine(_directory, "configured-libmpv.dll");
        File.WriteAllBytes(configuredPath, []);

        var resolved = LibMpvRuntime.FindLibraryPath(configuredPath, []);

        Assert.Equal(configuredPath, resolved);
    }

    [Fact]
    public void PackagedLibraryPathIsDiscoveredRelativeToTheApplicationBase()
    {
        var baseDirectory = Path.Combine(_directory, "app");
        var packagedPath = Path.Combine(baseDirectory, "runtime", "libmpv", "libmpv-2.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(packagedPath)!);
        File.WriteAllBytes(packagedPath, []);

        var resolved = LibMpvRuntime.FindLibraryPath(null, [], baseDirectory);

        Assert.Equal(packagedPath, resolved);
    }

    [Fact]
    public void CommandArgumentsPreserveUtf8MediaPathsAndTerminateWithNull()
    {
        using var arguments = new LibMpvArgumentArray(["loadfile", "C:\\媒体\\第01集.mkv", "replace"]);

        Assert.Equal(3, arguments.Count);
        Assert.Equal(["loadfile", "C:\\媒体\\第01集.mkv", "replace"], arguments.ToArray());
        Assert.Equal(IntPtr.Zero, Marshal.ReadIntPtr(arguments.Pointer, arguments.Count * IntPtr.Size));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
