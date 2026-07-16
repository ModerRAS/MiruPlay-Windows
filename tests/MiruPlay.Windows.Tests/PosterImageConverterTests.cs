using MiruPlay.Windows.Converters;

namespace MiruPlay.Windows.Tests;

public sealed class PosterImageConverterTests
{
    [Fact]
    public void LocalPosterIsLoadedWithoutKeepingTheFileLocked()
    {
        var path = Path.Combine(Path.GetTempPath(), $"miruplay-poster-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        try
        {
            var image = new PosterImageConverter().Convert(path, typeof(object), null!, System.Globalization.CultureInfo.InvariantCulture);

            Assert.NotNull(image);
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RemotePosterIsNotFetchedByWpfBinding()
    {
        var image = new PosterImageConverter().Convert(
            "https://example.invalid/poster.png",
            typeof(object),
            null!,
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Null(image);
    }
}
