using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class LibMpvBackendTests
{
    [Fact]
    public void ExistingMpvSessionImplementsTheBackendContract()
    {
        Assert.True(typeof(IPlaybackSession).IsAssignableFrom(typeof(LibMpvPlaybackSession)));
    }
}
