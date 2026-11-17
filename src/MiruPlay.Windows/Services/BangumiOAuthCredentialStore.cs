using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MiruPlay.Windows.Services;

/// <summary>OAuth app credentials. Secret material is DPAPI-protected and never logged,
/// echoed, or placed in URLs; only <see cref="Source"/> and a configured flag surface.</summary>
public sealed record BangumiOAuthCredentials(string ClientId, string ClientSecret, string Source);

public sealed class BangumiOAuthCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MiruPlay.Windows.BangumiOAuthCredentials.v1");
    private readonly string _path;

    public BangumiOAuthCredentialStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiruPlay",
            "bangumi-oauth.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public string FilePath => _path;

    public BangumiOAuthCredentials? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var encrypted = File.ReadAllBytes(_path);
            var plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var stored = JsonSerializer.Deserialize<StoredCredentials>(plaintext);
            if (stored is null || string.IsNullOrWhiteSpace(stored.ClientId) || string.IsNullOrWhiteSpace(stored.ClientSecret))
                return null;
            return new BangumiOAuthCredentials(stored.ClientId.Trim(), stored.ClientSecret.Trim(), "settings");
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            throw new InvalidDataException(AppStrings.Get("bgmOauth.credentialsBroken"), error);
        }
    }

    public void Save(string clientId, string clientSecret)
    {
        var id = clientId?.Trim() ?? string.Empty;
        var secret = clientSecret?.Trim() ?? string.Empty;
        if (id.Length is 0 or > 256) throw new ArgumentException(AppStrings.Get("bgmOauth.clientIdInvalid"), nameof(clientId));
        if (secret.Length is 0 or > 512) throw new ArgumentException(AppStrings.Get("bgmOauth.clientSecretInvalid"), nameof(clientSecret));
        Write(new StoredCredentials(id, secret));
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private void Write(StoredCredentials credentials)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials);
        var encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        var temporaryPath = $"{_path}.tmp";
        File.WriteAllBytes(temporaryPath, encrypted);
        File.Move(temporaryPath, _path, true);
    }

    private sealed record StoredCredentials(string ClientId, string ClientSecret);
}

public static class BangumiOAuthCredentialsProvider
{
    public const string ClientIdEnvironmentVariable = "BANGUMI_CLIENT_ID";
    public const string ClientSecretEnvironmentVariable = "BANGUMI_CLIENT_SECRET";

    /// <summary>Environment variables win; otherwise the DPAPI store is used. Null means
    /// "not configured" so callers degrade explicitly instead of breaking token features.</summary>
    public static BangumiOAuthCredentials? Resolve(
        BangumiOAuthCredentialStore store,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var read = environment ?? Environment.GetEnvironmentVariable;
        var id = read(ClientIdEnvironmentVariable)?.Trim();
        var secret = read(ClientSecretEnvironmentVariable)?.Trim();
        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(secret))
            return new BangumiOAuthCredentials(id, secret, "environment");
        return store.Load();
    }
}
