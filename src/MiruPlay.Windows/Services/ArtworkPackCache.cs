using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

internal sealed class ArtworkPackCache
{
    private const long MaximumPackBytes = 256L * 1024 * 1024;
    private const long MaximumAssetBytes = 256L * 1024 * 1024;
    private const long MaximumExtractedBytes = 256L * 1024 * 1024;
    private const int MaximumEntries = 4096;
    private readonly string _sourceDirectory;

    public ArtworkPackCache(string sourceDirectory)
    {
        _sourceDirectory = sourceDirectory;
    }

    public bool IsComplete(MlipArtworkPack pack)
    {
        var marker = MarkerPath(pack);
        if (!File.Exists(marker) || !File.ReadAllText(marker).Trim().Equals(pack.Sha256, StringComparison.OrdinalIgnoreCase))
            return false;
        return pack.Assets.All(IsValidCachedAsset);
    }

    public async Task ExtractAsync(
        MlipArtworkPack pack,
        Stream input,
        CancellationToken cancellationToken)
    {
        if (IsComplete(pack)) return;
        Directory.CreateDirectory(Path.Combine(_sourceDirectory, "packs"));
        Directory.CreateDirectory(Path.Combine(_sourceDirectory, "artwork"));
        var tarPath = Path.Combine(_sourceDirectory, "packs", $".{pack.Sha256}-{Guid.NewGuid():N}.tmp");
        var markerStaging = $"{MarkerPath(pack)}.{Guid.NewGuid():N}.tmp";
        try
        {
            var actualHash = await CopyAndHashAsync(input, tarPath, cancellationToken).ConfigureAwait(false);
            if (!actualHash.Equals(pack.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("MLIP artwork pack SHA-256 mismatch.");
            if (new FileInfo(tarPath).Length != pack.ByteSize)
                throw new InvalidDataException("MLIP artwork pack length does not match its catalog.");

            await ExtractTarAsync(tarPath, pack, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(markerStaging, pack.Sha256, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(markerStaging, MarkerPath(pack), true);
        }
        finally
        {
            if (File.Exists(tarPath)) File.Delete(tarPath);
            if (File.Exists(markerStaging)) File.Delete(markerStaging);
        }
    }

    private async Task ExtractTarAsync(string tarPath, MlipArtworkPack pack, CancellationToken cancellationToken)
    {
        var expected = pack.Assets.ToDictionary(asset => asset.MemberName, StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        await using var stream = new FileStream(
            tarPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[512];
        long extractedBytes = 0;
        var entries = 0;
        var zeroBlocks = 0;
        while (stream.Position < stream.Length)
        {
            await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
            if (header.All(value => value == 0))
            {
                zeroBlocks++;
                if (zeroBlocks == 2) break;
                continue;
            }
            if (zeroBlocks != 0) throw new InvalidDataException("MLIP artwork tar has an invalid end marker.");
            if (++entries > MaximumEntries) throw new InvalidDataException("MLIP artwork tar has too many entries.");
            ValidateChecksum(header);
            if (!ReadTarString(header.AsSpan(257, 6)).StartsWith("ustar", StringComparison.Ordinal))
                throw new InvalidDataException("MLIP artwork pack is not a standard tar archive.");
            var name = ReadTarString(header.AsSpan(0, 100));
            var prefix = ReadTarString(header.AsSpan(345, 155));
            if (prefix.Length != 0 || name.Length == 0 || name.Contains('/') || name.Contains('\\') || Path.GetFileName(name) != name)
                throw new InvalidDataException("MLIP artwork tar member path is unsafe.");
            if (header[156] is not (0 or (byte)'0'))
                throw new InvalidDataException("MLIP artwork tar contains a non-regular member.");
            var length = ParseOctal(header.AsSpan(124, 12));
            if (length <= 0 || length > MaximumAssetBytes)
                throw new InvalidDataException("MLIP artwork tar member exceeds safety limits.");
            extractedBytes += length;
            if (extractedBytes > MaximumExtractedBytes)
                throw new InvalidDataException("MLIP artwork tar extraction exceeds safety limits.");
            if (!expected.TryGetValue(name, out var asset) || !found.Add(name))
                throw new InvalidDataException("MLIP artwork tar contains an unexpected or duplicate member.");
            if (asset.DataOffset != stream.Position || asset.DataLength != length)
                throw new InvalidDataException("MLIP artwork tar offsets do not match its catalog.");

            var stagingPath = Path.Combine(_sourceDirectory, "artwork", $".{asset.Sha256}-{Guid.NewGuid():N}.tmp");
            try
            {
                var hash = await CopyMemberAsync(stream, stagingPath, length, cancellationToken).ConfigureAwait(false);
                if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("MLIP artwork asset SHA-256 mismatch.");
                ValidateImage(stagingPath, asset);
                File.Move(stagingPath, AssetPath(asset), true);
            }
            finally
            {
                if (File.Exists(stagingPath)) File.Delete(stagingPath);
            }

            var padding = (512 - length % 512) % 512;
            if (padding > 0) stream.Seek(padding, SeekOrigin.Current);
        }
        if (zeroBlocks != 2 || entries != pack.EntryCount || found.Count != expected.Count)
            throw new InvalidDataException("MLIP artwork tar is incomplete.");
        while (stream.Position < stream.Length)
        {
            await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
            if (header.Any(value => value != 0))
                throw new InvalidDataException("MLIP artwork tar has nonzero trailing data.");
        }
    }

    private bool IsValidCachedAsset(MlipArtworkAsset asset)
    {
        var path = AssetPath(asset);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length != asset.DataLength) return false;
            using var input = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            ValidateImage(path, asset);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private string AssetPath(MlipArtworkAsset asset) =>
        Path.Combine(_sourceDirectory, "artwork", $"{asset.Sha256}{asset.Extension}");

    private string MarkerPath(MlipArtworkPack pack) =>
        Path.Combine(_sourceDirectory, "packs", $"{pack.Sha256}.complete");

    private static async Task<string> CopyAndHashAsync(Stream input, string path, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaximumPackBytes) throw new InvalidDataException("MLIP artwork pack exceeds 256 MiB.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string> CopyMemberAsync(
        Stream input,
        string path,
        long length,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("MLIP artwork tar ended inside a member.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void ValidateImage(string path, MlipArtworkAsset asset)
    {
        var expectedMediaType = asset.Extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => throw new InvalidDataException("MLIP artwork extension is unsupported."),
        };
        if (!asset.MediaType.Equals(expectedMediaType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("MLIP artwork MIME type does not match its member name.");
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> signature = stackalloc byte[12];
            if (input.Read(signature) < signature.Length || !HasExpectedSignature(signature, expectedMediaType))
                throw new InvalidDataException("MLIP artwork bytes do not match the catalog MIME type.");
            input.Position = 0;
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 16_384 || frame.PixelHeight > 16_384 ||
                (long)frame.PixelWidth * frame.PixelHeight > 100_000_000)
                throw new InvalidDataException("MLIP artwork dimensions exceed safety limits.");
            if (asset.Width is int width && frame.PixelWidth != width || asset.Height is int height && frame.PixelHeight != height)
                throw new InvalidDataException("MLIP artwork dimensions do not match its catalog.");
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException)
        {
            throw new InvalidDataException("MLIP artwork is not a decodable image.", error);
        }
    }

    private static bool HasExpectedSignature(ReadOnlySpan<byte> value, string mediaType) => mediaType switch
    {
        "image/jpeg" => value[0] == 0xff && value[1] == 0xd8 && value[2] == 0xff,
        "image/png" => value[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
        "image/webp" => value[..4].SequenceEqual("RIFF"u8) && value[8..12].SequenceEqual("WEBP"u8),
        _ => false,
    };

    private static void ValidateChecksum(byte[] header)
    {
        var expected = ParseOctal(header.AsSpan(148, 8));
        long actual = 0;
        for (var index = 0; index < header.Length; index++)
            actual += index is >= 148 and < 156 ? (byte)' ' : header[index];
        if (actual != expected) throw new InvalidDataException("MLIP artwork tar checksum is invalid.");
    }

    private static string ReadTarString(ReadOnlySpan<byte> value)
    {
        var end = value.IndexOf((byte)0);
        if (end < 0) end = value.Length;
        return Encoding.ASCII.GetString(value[..end]);
    }

    private static long ParseOctal(ReadOnlySpan<byte> value)
    {
        var text = Encoding.ASCII.GetString(value).Trim('\0', ' ');
        if (text.Length == 0) return 0;
        if (text.Any(ch => ch is < '0' or > '7'))
            throw new InvalidDataException("MLIP artwork tar contains an invalid octal field.");
        try { return Convert.ToInt64(text, 8); }
        catch (OverflowException error)
        {
            throw new InvalidDataException("MLIP artwork tar contains an invalid octal field.", error);
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("MLIP artwork tar is truncated.");
            read += count;
        }
    }
}
