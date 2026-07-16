using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MiruPlay.Windows.Services;

public sealed record CloudDriveCredentials(
    string? EndpointUrl = null,
    string? Token = null,
    string? Password = null);

public sealed class CloudDriveCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MiruPlay.Windows.CloudDriveCredentials.v1");
    private readonly string _path;

    public CloudDriveCredentialStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiruPlay", "cloud-drive-credentials.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
    }

    public CloudDriveCredentials Load()
    {
        if (!File.Exists(_path)) return new CloudDriveCredentials();
        try
        {
            var encrypted = File.ReadAllBytes(_path);
            var plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<CloudDriveCredentials>(plaintext) ?? new CloudDriveCredentials();
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            throw new InvalidDataException("CloudDrive 凭据无法解密。", error);
        }
    }

    public CloudDriveCredentials LoadForEndpoint(string endpointUrl)
    {
        var normalizedEndpoint = NormalizeEndpoint(endpointUrl);
        var credentials = Load();
        if (!string.Equals(credentials.EndpointUrl, normalizedEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("CloudDrive2 凭据未在当前服务地址验证；请重新登录或验证 Token。");
        }
        return credentials;
    }

    public void SaveToken(string endpointUrl, string token) =>
        SaveForEndpoint(endpointUrl, credentials => credentials with { Token = ValidateToken(token) });

    public void SavePassword(string endpointUrl, string password) =>
        SaveForEndpoint(endpointUrl, credentials => credentials with { Password = ValidatePassword(password) });

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private void SaveForEndpoint(string endpointUrl, Func<CloudDriveCredentials, CloudDriveCredentials> update)
    {
        var normalizedEndpoint = NormalizeEndpoint(endpointUrl);
        var current = Load();
        if (!string.Equals(current.EndpointUrl, normalizedEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            current = new CloudDriveCredentials(EndpointUrl: normalizedEndpoint);
        }
        Save(update(current));
    }

    private static string NormalizeEndpoint(string endpointUrl) =>
        CloudDriveGrpcClient.ValidateEndpoint(endpointUrl).AbsoluteUri.TrimEnd('/');

    private static string ValidateToken(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length is 0 or > 4_096) throw new ArgumentException("CloudDrive2 API Token 长度无效。", nameof(value));
        return normalized;
    }

    private static string ValidatePassword(string value)
    {
        if (value.Length is 0 or > 4_096) throw new ArgumentException("CloudDrive2 密码长度无效。", nameof(value));
        return value;
    }

    private void Save(CloudDriveCredentials credentials)
    {
        var encrypted = ProtectedData.Protect(
            JsonSerializer.SerializeToUtf8Bytes(credentials),
            Entropy,
            DataProtectionScope.CurrentUser);
        var temporaryPath = $"{_path}.tmp";
        File.WriteAllBytes(temporaryPath, encrypted);
        File.Move(temporaryPath, _path, true);
    }
}
