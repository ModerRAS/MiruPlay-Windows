using System.Security.Cryptography;
using System.Text;

namespace MiruPlay.Windows.Services;

public sealed class WebControlTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MiruPlay.Windows.WebControl.v1");
    private readonly string _tokenPath;

    public WebControlTokenStore(string? tokenPath = null)
    {
        if (tokenPath is null)
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MiruPlay");
            Directory.CreateDirectory(directory);
            tokenPath = Path.Combine(directory, "web-control-token.bin");
        }
        _tokenPath = tokenPath;
        AccessToken = LoadOrRotate();
    }

    public string AccessToken { get; private set; }

    public string Rotate()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        AccessToken = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(AccessToken), Entropy, DataProtectionScope.CurrentUser);
        var tempPath = $"{_tokenPath}.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_tokenPath))!);
        File.WriteAllBytes(tempPath, encrypted);
        File.Move(tempPath, _tokenPath, true);
        return AccessToken;
    }

    public bool Matches(string? candidate)
    {
        if (candidate is null) return false;
        var expectedBytes = Encoding.UTF8.GetBytes(AccessToken);
        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        return expectedBytes.Length == candidateBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, candidateBytes);
    }

    private string LoadOrRotate()
    {
        if (File.Exists(_tokenPath))
        {
            try
            {
                var encrypted = File.ReadAllBytes(_tokenPath);
                var decrypted = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                var token = Encoding.UTF8.GetString(decrypted);
                if (!string.IsNullOrWhiteSpace(token)) return token;
            }
            catch (CryptographicException)
            {
                // Rotate unreadable credentials instead of weakening storage.
            }
        }
        return Rotate();
    }
}
