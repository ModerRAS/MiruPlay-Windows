using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MiruPlay.Windows.Services;

public sealed record MetadataTokens(string? Bangumi = null, string? Tmdb = null);

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
            throw new InvalidDataException("元数据令牌无法解密。", error);
        }
    }

    public void SaveBangumi(string token)
    {
        var value = ValidateToken(token, "Bangumi Access Token");
        Save(Load() with { Bangumi = value });
    }

    public void ClearBangumi() => Save(Load() with { Bangumi = null });

    public void SaveTmdb(string token)
    {
        var value = ValidateToken(token, "TMDB Read Access Token");
        Save(Load() with { Tmdb = value });
    }

    public void ClearTmdb() => Save(Load() with { Tmdb = null });

    private static string ValidateToken(string token, string label)
    {
        var value = token.Trim();
        if (value.Length == 0) throw new ArgumentException($"{label} 不能为空。", nameof(token));
        if (value.Length > 4_096) throw new ArgumentException($"{label} 长度无效。", nameof(token));
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
