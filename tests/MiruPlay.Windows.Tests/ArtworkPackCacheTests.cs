using System.Security.Cryptography;
using System.Text;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class ArtworkPackCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miruplay-pack-{Guid.NewGuid():N}");
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task ExtractsVerifiedPackOnceAndWritesSourceScopedMarker()
    {
        var assetHash = Hash(Png);
        var member = $"{assetHash}.png";
        var tar = CreateTar(member, Png);
        var asset = new MlipArtworkAsset(1, assetHash, 1, member, 512, Png.Length, "image/png", 1, 1);
        var pack = new MlipArtworkPack(1, "MLIP-Artwork/one.tar", Hash(tar), tar.Length, 1, [asset]);
        var cache = new ArtworkPackCache(_root);

        await cache.ExtractAsync(pack, new MemoryStream(tar), CancellationToken.None);
        await cache.ExtractAsync(pack, new ThrowingStream(), CancellationToken.None);

        Assert.Equal(Png, await File.ReadAllBytesAsync(Path.Combine(_root, "artwork", $"{assetHash}.png")));
        Assert.True(File.Exists(Path.Combine(_root, "packs", $"{pack.Sha256}.complete")));
    }

    [Fact]
    public async Task CorruptCachedAssetIsReplacedFromTheVerifiedPack()
    {
        var assetHash = Hash(Png);
        var member = $"{assetHash}.png";
        var tar = CreateTar(member, Png);
        var asset = new MlipArtworkAsset(1, assetHash, 1, member, 512, Png.Length, "image/png", 1, 1);
        var pack = new MlipArtworkPack(1, "MLIP-Artwork/one.tar", Hash(tar), tar.Length, 1, [asset]);
        var cache = new ArtworkPackCache(_root);
        await cache.ExtractAsync(pack, new MemoryStream(tar), CancellationToken.None);
        var path = Path.Combine(_root, "artwork", $"{assetHash}.png");
        await File.WriteAllBytesAsync(path, new byte[Png.Length]);

        Assert.False(cache.IsComplete(pack));
        await cache.ExtractAsync(pack, new MemoryStream(tar), CancellationToken.None);

        Assert.True(cache.IsComplete(pack));
        Assert.Equal(Png, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task HashMismatchAndUnsafeTarDoNotCreateCompletionMarker()
    {
        var assetHash = Hash(Png);
        var member = $"{assetHash}.png";
        var validTar = CreateTar(member, Png);
        var asset = new MlipArtworkAsset(1, assetHash, 1, member, 512, Png.Length, "image/png", 1, 1);
        var badHashPack = new MlipArtworkPack(1, "MLIP-Artwork/one.tar", new string('0', 64), validTar.Length, 1, [asset]);
        var cache = new ArtworkPackCache(_root);

        await Assert.ThrowsAsync<InvalidDataException>(() => cache.ExtractAsync(
            badHashPack,
            new MemoryStream(validTar),
            CancellationToken.None));

        var unsafeTar = CreateTar("../poster.png", Png);
        var unsafePack = badHashPack with { Sha256 = Hash(unsafeTar), ByteSize = unsafeTar.Length };
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.ExtractAsync(
            unsafePack,
            new MemoryStream(unsafeTar),
            CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_root, "packs", $"{unsafePack.Sha256}.complete")));
    }

    private static byte[] CreateTar(string name, byte[] content)
    {
        using var output = new MemoryStream();
        var header = new byte[512];
        Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
        WriteOctal(header, 100, 8, 0x1A4);
        WriteOctal(header, 108, 8, 0);
        WriteOctal(header, 116, 8, 0);
        WriteOctal(header, 124, 12, content.Length);
        WriteOctal(header, 136, 12, 0);
        Array.Fill(header, (byte)' ', 148, 8);
        header[156] = (byte)'0';
        Encoding.ASCII.GetBytes("ustar\0").CopyTo(header, 257);
        Encoding.ASCII.GetBytes("00").CopyTo(header, 263);
        var checksum = header.Sum(value => (int)value);
        WriteOctal(header, 148, 8, checksum);
        output.Write(header);
        output.Write(content);
        output.Write(new byte[(512 - content.Length % 512) % 512]);
        output.Write(new byte[1024]);
        return output.ToArray();
    }

    private static void WriteOctal(byte[] target, int offset, int length, long value)
    {
        var text = Convert.ToString(value, 8)!.PadLeft(length - 2, '0');
        Encoding.ASCII.GetBytes(text).CopyTo(target, offset);
        target[offset + length - 2] = 0;
        target[offset + length - 1] = (byte)' ';
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Pack was read twice.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new InvalidOperationException("Pack was read twice."));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
