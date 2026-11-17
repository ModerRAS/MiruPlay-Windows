using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MiruPlay.Windows.Services;

public sealed record MetadataTokens(
    string? Bangumi = null,
    string? Tmdb = null,
    string? BangumiRefreshToken = null,
    long BangumiExpiresAtMillis = 0);

public sealed class MetadataTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MiruPlay.Windows.MetadataTokens.v1");
    private readonly string _path;

    public MetadataTokenStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiruPlay",
            "metadata-tokens.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public MetadataTokens Load()
    {
        if (!File.Exists(_path)) return new MetadataTokens();
        try
        {
            var encrypted = File.ReadAllBytes(_path);
            var plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<MetadataTokens>(plaintext) ?? new MetadataTokens();
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            throw new InvalidDataException(AppStrings.Get("meta.tokenBroken"), error);
        }
    }

    public void SaveBangumi(string token)
    {
        var value = ValidateToken(token, "Bangumi Access Token");
        // A manually pasted token has no refresh/expiry metadata; clear stale OAuth state.
        Save(Load() with { Bangumi = value, BangumiRefreshToken = null, BangumiExpiresAtMillis = 0 });
    }

    /// <summary>Persists an OAuth token pair (access token + rotating refresh token) with the
    /// absolute expiry timestamp. Values stay DPAPI-protected inside the same vault file.</summary>
    public void SaveBangumiSession(string accessToken, string refreshToken, long expiresAtMillis)
    {
        var access = ValidateToken(accessToken, "Bangumi Access Token");
        var refresh = refreshToken?.Trim() ?? string.Empty;
        if (refresh.Length > 4_096) throw new ArgumentException(AppStrings.Get("meta.tokenLength", "Bangumi Refresh Token"), nameof(refreshToken));
        Save(Load() with
        {
            Bangumi = access,
            BangumiRefreshToken = refresh.Length == 0 ? null : refresh,
            BangumiExpiresAtMillis = expiresAtMillis > 0 ? expiresAtMillis : 0,
        });
    }

    public void ClearBangumi() => Save(Load() with
    {
        Bangumi = null,
        BangumiRefreshToken = null,
        BangumiExpiresAtMillis = 0,
    });

    public void SaveTmdb(string token)
    {
        var value = ValidateToken(token, "TMDB Read Access Token");
        Save(Load() with { Tmdb = value });
    }

    public void ClearTmdb() => Save(Load() with { Tmdb = null });

    private static string ValidateToken(string token, string label)
    {
        var value = token.Trim();
        if (value.Length == 0) throw new ArgumentException(AppStrings.Get("meta.tokenEmpty", label), nameof(token));
        if (value.Length > 4_096) throw new ArgumentException(AppStrings.Get("meta.tokenLength", label), nameof(token));
        return value;
    }

    private void Save(MetadataTokens tokens)
    {
        if (string.IsNullOrEmpty(tokens.Bangumi) && string.IsNullOrEmpty(tokens.Tmdb))
        {
            if (File.Exists(_path)) File.Delete(_path);
            return;
        }
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(tokens);
        var encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        var temporaryPath = $"{_path}.tmp";
        File.WriteAllBytes(temporaryPath, encrypted);
        File.Move(temporaryPath, _path, true);
    }
}
