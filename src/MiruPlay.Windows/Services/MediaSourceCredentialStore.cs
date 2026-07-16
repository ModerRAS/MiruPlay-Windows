using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MiruPlay.Windows.Services;

public sealed record MediaSourceCredential(string Username, string Password, string? Domain = null)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Username) && string.IsNullOrEmpty(Password);
}

public sealed class MediaSourceCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MiruPlay.Windows.MediaSourceCredentials.v1");
    private readonly string _directory;

    public MediaSourceCredentialStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiruPlay",
            "source-credentials");
        Directory.CreateDirectory(_directory);
    }

    public MediaSourceCredential? Get(long sourceId)
    {
        var path = PathFor(sourceId);
        if (!File.Exists(path)) return null;
        try
        {
            var encrypted = File.ReadAllBytes(path);
            var decrypted = ProtectedData.Unprotect(encrypted, EntropyFor(sourceId), DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<MediaSourceCredential>(decrypted);
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            throw new InvalidDataException("媒体源凭据无法解密。", error);
        }
    }

    public void Save(long sourceId, MediaSourceCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (credential.IsEmpty)
        {
            Delete(sourceId);
            return;
        }
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credential);
        var encrypted = ProtectedData.Protect(plaintext, EntropyFor(sourceId), DataProtectionScope.CurrentUser);
        var path = PathFor(sourceId);
        var tempPath = $"{path}.tmp";
        File.WriteAllBytes(tempPath, encrypted);
        File.Move(tempPath, path, true);
    }

    public void Delete(long sourceId)
    {
        var path = PathFor(sourceId);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(long sourceId) => Path.Combine(_directory, $"source-{sourceId}.bin");

    private static byte[] EntropyFor(long sourceId) =>
        Entropy.Concat(BitConverter.GetBytes(sourceId)).ToArray();
}
