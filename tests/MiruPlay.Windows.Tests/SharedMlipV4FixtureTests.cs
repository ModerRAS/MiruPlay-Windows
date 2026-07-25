using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class SharedMlipV4FixtureTests
{
    [Fact]
    public void RustGeneratedBaseAndIncrementalFixturesUseTheSharedContract()
    {
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mlip-v4");

        var baseCatalog = MlipLibraryReader.Load(Path.Combine(fixtureRoot, "base"));
        Assert.Equal(4, baseCatalog.SchemaVersion);
        Assert.Single(baseCatalog.ArtworkPacks);
        Assert.Equal(4, baseCatalog.ArtworkBindings.Count);
        Assert.Equal(2, baseCatalog.ArtworkPacks.SelectMany(pack => pack.Assets).Count());
        Assert.Contains(baseCatalog.ArtworkBindings, binding =>
            binding.OwnerKind == "series" && binding.ArtworkKind == 7 && binding.Reference is not null);
        Assert.Contains(baseCatalog.ArtworkBindings, binding =>
            binding.OwnerKind == "episode" && binding.ArtworkKind == 5 && binding.Reference is not null);
        Assert.Contains(baseCatalog.ArtworkBindings, binding =>
            binding.Reference?.SourceUrl == "https://lain.bgm.tv/pic/cover/l/fixture-original.jpg" &&
            binding.Reference.SourceSubjectId == "424242");
        Assert.Contains(baseCatalog.ArtworkBindings, binding =>
            binding.ArtworkKind == 4 && binding.Reference is null && binding.LegacyPath is not null);

        var incrementalCatalog = MlipLibraryReader.Load(Path.Combine(fixtureRoot, "incremental"));
        Assert.Equal(2, incrementalCatalog.ArtworkPacks.Count);
        Assert.Equal(5, incrementalCatalog.ArtworkBindings.Count);
        Assert.Equal(3, incrementalCatalog.ArtworkPacks.SelectMany(pack => pack.Assets).Count());
        Assert.Single(
            incrementalCatalog.ArtworkPacks.Select(pack => pack.Sha256)
                .Except(baseCatalog.ArtworkPacks.Select(pack => pack.Sha256)));
    }
}
