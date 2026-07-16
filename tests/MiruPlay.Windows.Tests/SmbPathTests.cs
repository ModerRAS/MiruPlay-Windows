using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class SmbPathTests
{
    [Theory]
    [InlineData(@"\\nas.local\anime\Season 1\", "smb://nas.local/anime/Season%201")]
    [InlineData("smb://nas.local/anime/Season%201/", "smb://nas.local/anime/Season%201")]
    [InlineData("nas.local/anime", "smb://nas.local/anime")]
    public void NormalizeAcceptsUncAndSmbLocations(string input, string expected) =>
        Assert.Equal(expected, SmbPath.NormalizeRoot(input));

    [Fact]
    public void ResolvesMlipPathsUnderTheShareRoot()
    {
        const string root = "smb://nas.local/anime/Season%201";

        Assert.Equal(
            @"\\nas.local\anime\Season 1\Frieren\01.mkv",
            SmbPath.ResolveIndexPath(root, "/Frieren/01.mkv"));
        Assert.Equal(@"\\nas.local\anime\Season 1", SmbPath.ToUncPath(root));
        Assert.Equal(@"\\nas.local\anime", SmbPath.ShareRoot(root));
    }

    [Theory]
    [InlineData("smb://nas/share/../private")]
    [InlineData("smb://user:secret@nas/share")]
    [InlineData("https://nas/share")]
    [InlineData("smb://nas")]
    public void RejectsUnsafeOrIncompleteLocations(string input) =>
        Assert.Throws<ArgumentException>(() => SmbPath.NormalizeRoot(input));

    [Theory]
    [InlineData("../secret.mkv")]
    [InlineData("smb://other/share/file.mkv")]
    [InlineData(@"\\other\share\file.mkv")]
    public void RejectsUnsafeMlipPaths(string indexPath) =>
        Assert.Throws<InvalidDataException>(() => SmbPath.ResolveIndexPath("smb://nas/anime", indexPath));
}
