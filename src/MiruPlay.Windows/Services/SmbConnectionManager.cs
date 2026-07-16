using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MiruPlay.Windows.Services;

public sealed class SmbConnectionManager : IDisposable
{
    private const int ResourceTypeDisk = 1;
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ErrorNotConnected = 2250;
    private readonly HashSet<string> _ownedShares = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public void EnsureConnected(
        string location,
        MediaSourceCredential? credential,
        bool replaceOwnedConnection = false)
    {
        var uncPath = SmbPath.ToUncPath(location);
        if (credential is { IsEmpty: false } && string.IsNullOrWhiteSpace(credential.Username))
        {
            throw new InvalidDataException("SMB 密码需要对应的用户名。");
        }

        var shareRoot = SmbPath.ShareRoot(location);
        var resource = new NativeNetworkResource
        {
            Type = ResourceTypeDisk,
            RemoteName = shareRoot,
        };
        var username = BuildUsername(credential);
        lock (_sync)
        {
            if (replaceOwnedConnection)
            {
                if (_ownedShares.Remove(shareRoot))
                {
                    var cancelResult = WNetCancelConnection2(shareRoot, 0, true);
                    if (cancelResult is not (0 or ErrorNotConnected))
                    {
                        throw new IOException("无法替换 SMB 凭据。", new Win32Exception(cancelResult));
                    }
                }
                else if (Directory.Exists(uncPath))
                {
                    throw new InvalidOperationException("Windows 已使用其他凭据连接该 SMB 共享，且连接不由 MiruPlay 所有。请先在 Windows 中断开该共享。");
                }
            }
            if (Directory.Exists(uncPath)) return;
            var result = WNetAddConnection2(ref resource, credential?.Password, username, 0);
            if (result == ErrorSessionCredentialConflict && Directory.Exists(uncPath)) return;
            if (result != 0)
            {
                var detail = result == ErrorSessionCredentialConflict
                    ? "Windows 已使用不同凭据连接到该 SMB 服务器。请先断开现有连接。"
                    : new Win32Exception(result).Message;
                throw new IOException($"无法连接 SMB 共享：{detail}", new Win32Exception(result));
            }
            _ownedShares.Add(shareRoot);
        }

        if (!Directory.Exists(uncPath))
        {
            Disconnect(location);
            throw new DirectoryNotFoundException("SMB 目录不存在或当前账户无权访问。");
        }
    }

    public void Disconnect(string location)
    {
        var shareRoot = SmbPath.ShareRoot(location);
        lock (_sync)
        {
            if (!_ownedShares.Remove(shareRoot)) return;
            _ = WNetCancelConnection2(shareRoot, 0, true);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var share in _ownedShares) _ = WNetCancelConnection2(share, 0, true);
            _ownedShares.Clear();
        }
        GC.SuppressFinalize(this);
    }

    private static string? BuildUsername(MediaSourceCredential? credential)
    {
        if (credential is null || string.IsNullOrWhiteSpace(credential.Username)) return null;
        if (string.IsNullOrWhiteSpace(credential.Domain) ||
            credential.Username.Contains('\\') ||
            credential.Username.Contains('@')) return credential.Username;
        return $"{credential.Domain}\\{credential.Username}";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeNetworkResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(
        ref NativeNetworkResource networkResource,
        string? password,
        string? username,
        int flags);

    [DllImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);
}
