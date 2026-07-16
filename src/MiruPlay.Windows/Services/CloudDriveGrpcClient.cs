using Grpc.Core;
using Grpc.Net.Client;
using MiruPlay.Windows.CloudDriveProtocol;

namespace MiruPlay.Windows.Services;

public sealed record CloudDriveLoginResult(string Token);

public sealed record CloudDriveFileInfo(string Name, string Path, bool IsDirectory, long Size);

public sealed record CloudDriveTokenInfo(
    string RootDir,
    string FriendlyName,
    bool AllowList,
    bool AllowCreateFolder,
    bool AllowCreateFile,
    bool AllowWrite,
    bool AllowMove,
    bool AllowAddOfflineDownload);

public sealed class CloudDriveGrpcClient
{
    private readonly Func<Uri, string, string, CancellationToken, Task<CloudDriveLoginResult>>? _login;
    private readonly Func<Uri, string, CancellationToken, Task<CloudDriveTokenInfo>>? _tokenInfo;
    private readonly Func<Uri, string, string, bool, CancellationToken, Task<IReadOnlyList<CloudDriveFileInfo>>>? _listFolder;
    private readonly Func<Uri, string, IReadOnlyList<string>, string, CancellationToken, Task>? _addOfflineFiles;
    private readonly Func<Uri, string, string, string, CancellationToken, Task>? _createFolder;
    private readonly Func<Uri, string, ReadOnlyMemory<byte>, string, string, CancellationToken, Task<string>>? _uploadFile;
    private readonly Func<Uri, string, IReadOnlyList<string>, string, CancellationToken, Task>? _moveFiles;

    public CloudDriveGrpcClient()
    {
    }

    internal CloudDriveGrpcClient(
        Func<Uri, string, string, CancellationToken, Task<CloudDriveLoginResult>> login,
        Func<Uri, string, CancellationToken, Task<CloudDriveTokenInfo>> tokenInfo,
        Func<Uri, string, string, bool, CancellationToken, Task<IReadOnlyList<CloudDriveFileInfo>>>? listFolder = null,
        Func<Uri, string, IReadOnlyList<string>, string, CancellationToken, Task>? addOfflineFiles = null,
        Func<Uri, string, string, string, CancellationToken, Task>? createFolder = null,
        Func<Uri, string, ReadOnlyMemory<byte>, string, string, CancellationToken, Task<string>>? uploadFile = null,
        Func<Uri, string, IReadOnlyList<string>, string, CancellationToken, Task>? moveFiles = null)
    {
        _login = login;
        _tokenInfo = tokenInfo;
        _listFolder = listFolder;
        _addOfflineFiles = addOfflineFiles;
        _createFolder = createFolder;
        _uploadFile = uploadFile;
        _moveFiles = moveFiles;
    }

    public Task<CloudDriveLoginResult> LoginAsync(
        string endpointUrl,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var endpoint = ValidateEndpoint(endpointUrl);
        var user = username.Trim();
        if (user.Length == 0 || string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("CloudDrive2 用户名和密码不能为空。");
        return _login is null
            ? LoginGrpcAsync(endpoint, user, password, cancellationToken)
            : _login(endpoint, user, password, cancellationToken);
    }

    public Task<CloudDriveTokenInfo> GetApiTokenInfoAsync(
        string endpointUrl,
        string token,
        CancellationToken cancellationToken = default)
    {
        var endpoint = ValidateEndpoint(endpointUrl);
        var value = token.Trim();
        if (value.Length == 0) throw new ArgumentException("CloudDrive2 API Token 不能为空。", nameof(token));
        return _tokenInfo is null
            ? GetApiTokenInfoGrpcAsync(endpoint, value, cancellationToken)
            : _tokenInfo(endpoint, value, cancellationToken);
    }

    public Task<IReadOnlyList<CloudDriveFileInfo>> ListFolderAsync(
        string endpointUrl,
        string token,
        string path,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var endpoint = ValidateEndpoint(endpointUrl);
        var value = token.Trim();
        if (value.Length == 0) throw new ArgumentException("CloudDrive2 API Token 不能为空。", nameof(token));
        var normalizedPath = NormalizePath(path);
        return _listFolder is null
            ? ListFolderGrpcAsync(endpoint, value, normalizedPath, forceRefresh, cancellationToken)
            : _listFolder(endpoint, value, normalizedPath, forceRefresh, cancellationToken);
    }

    public Task AddOfflineFilesAsync(
        string endpointUrl,
        string token,
        IReadOnlyList<string> urls,
        string targetFolder,
        CancellationToken cancellationToken = default)
    {
        var endpoint = ValidateEndpoint(endpointUrl);
        var value = token.Trim();
        if (value.Length == 0) throw new ArgumentException("CloudDrive2 API Token 不能为空。", nameof(token));
        var normalizedUrls = NormalizeOfflineUrls(urls);
        var target = NormalizePath(targetFolder);
        if (target == "/") throw new ArgumentException("CloudDrive2 离线下载目录不能是根目录。", nameof(targetFolder));
        return _addOfflineFiles is null
            ? AddOfflineFilesGrpcAsync(endpoint, value, normalizedUrls, target, cancellationToken)
            : _addOfflineFiles(endpoint, value, normalizedUrls, target, cancellationToken);
    }

    public Task CreateFolderAsync(
        string endpointUrl,
        string token,
        string parentPath,
        string folderName,
        CancellationToken cancellationToken = default)
    {
        var endpoint = ValidateEndpoint(endpointUrl);
        var value = token.Trim();
        if (value.Length == 0) throw new ArgumentException("CloudDrive2 API Token 不能为空。", nameof(token));
        var parent = NormalizePath(parentPath);
        var name = NormalizeFileName(folderName, nameof(folderName));
        return _createFolder is null
            ? CreateFolderGrpcAsync(endpoint, value, parent, name, cancellationToken)
            : _createFolder(endpoint, value, parent, name, cancellationToken);
    }

    public Task<string> UploadFileAsync(
        string endpointUrl,
        string token,
        ReadOnlyMemory<byte> content,
        string parentPath,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (content.Length is < 1 or > 16 * 1024 * 1024) throw new ArgumentException("CloudDrive2 上传文件必须为 1 字节到 16 MiB。", nameof(content));
        var endpoint = ValidateEndpoint(endpointUrl);
        var value = token.Trim();
        if (value.Length == 0) throw new ArgumentException("CloudDrive2 API Token 不能为空。", nameof(token));
        var parent = NormalizePath(parentPath);
        var name = NormalizeFileName(fileName, nameof(fileName));
        return _uploadFile is null
            ? UploadFileGrpcAsync(endpoint, value, content, parent, name, cancellationToken)
            : _uploadFile(endpoint, value, content, parent, name, cancellationToken);
    }

    public Task MoveFilesAsync(
        string endpointUrl,
        string token,
        IReadOnlyList<string> paths,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count is < 1 or > 100) throw new ArgumentException("CloudDrive2 移动文件数量必须为 1 到 100。", nameof(paths));
        var endpoint = ValidateEndpoint(endpointUrl);
        var value = token.Trim();
        if (value.Length == 0) throw new ArgumentException("CloudDrive2 API Token 不能为空。", nameof(token));
        var normalizedPaths = paths.Select(NormalizePath).ToList();
        if (normalizedPaths.Any(path => path == "/")) throw new ArgumentException("CloudDrive2 不能移动根目录。", nameof(paths));
        var destination = NormalizePath(destinationPath);
        if (destination == "/") throw new ArgumentException("CloudDrive2 移动目标不能是根目录。", nameof(destinationPath));
        return _moveFiles is null
            ? MoveFilesGrpcAsync(endpoint, value, normalizedPaths, destination, cancellationToken)
            : _moveFiles(endpoint, value, normalizedPaths, destination, cancellationToken);
    }

    private static async Task<CloudDriveLoginResult> LoginGrpcAsync(
        Uri endpoint,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using var channel = GrpcChannel.ForAddress(endpoint);
        var client = new CloudDriveFileSrv.CloudDriveFileSrvClient(channel);
        try
        {
            var response = await client.GetTokenAsync(
                new GetTokenRequest { UserName = username, Password = password },
                deadline: DateTime.UtcNow.AddSeconds(20),
                cancellationToken: cancellationToken);
            if (!response.Success || string.IsNullOrWhiteSpace(response.Token))
                throw new InvalidOperationException($"CloudDrive2 登录失败：{response.ErrorMessage.Trim()}");
            return new CloudDriveLoginResult(response.Token.Trim());
        }
        catch (RpcException error)
        {
            throw new HttpRequestException($"CloudDrive2 登录请求失败 ({error.StatusCode})。", error);
        }
    }

    private static async Task<CloudDriveTokenInfo> GetApiTokenInfoGrpcAsync(
        Uri endpoint,
        string token,
        CancellationToken cancellationToken)
    {
        using var channel = GrpcChannel.ForAddress(endpoint);
        var client = new CloudDriveFileSrv.CloudDriveFileSrvClient(channel);
        try
        {
            var response = await client.GetApiTokenInfoAsync(
                new StringValue { Value = token },
                deadline: DateTime.UtcNow.AddSeconds(20),
                cancellationToken: cancellationToken);
            var permissions = response.Permissions ?? new TokenPermissions();
            return new CloudDriveTokenInfo(
                response.RootDir,
                response.FriendlyName,
                permissions.AllowList,
                permissions.AllowCreateFolder,
                permissions.AllowCreateFile,
                permissions.AllowWrite,
                permissions.AllowMove,
                permissions.AllowAddOfflineDownload);
        }
        catch (RpcException error)
        {
            throw new HttpRequestException($"CloudDrive2 API Token 验证失败 ({error.StatusCode})。", error);
        }
    }

    private static async Task<IReadOnlyList<CloudDriveFileInfo>> ListFolderGrpcAsync(
        Uri endpoint,
        string token,
        string path,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ListFolderGrpcAttemptAsync(endpoint, $"Bearer {token}", path, forceRefresh, cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unauthenticated || error.Status.Detail.Contains("Invalid auth token", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return await ListFolderGrpcAttemptAsync(endpoint, token, path, forceRefresh, cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException retryError)
            {
                throw new HttpRequestException($"CloudDrive2 目录读取失败 ({retryError.StatusCode})。", retryError);
            }
        }
        catch (RpcException error)
        {
            throw new HttpRequestException($"CloudDrive2 目录读取失败 ({error.StatusCode})。", error);
        }
    }

    private static async Task<IReadOnlyList<CloudDriveFileInfo>> ListFolderGrpcAttemptAsync(
        Uri endpoint,
        string authorization,
        string path,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        using var channel = GrpcChannel.ForAddress(endpoint);
        var client = new CloudDriveFileSrv.CloudDriveFileSrvClient(channel);
        var headers = new Metadata { { "Authorization", authorization } };
        using var call = client.GetSubFiles(
            new ListSubFileRequest { Path = path, ForceRefresh = forceRefresh },
            headers,
            deadline: DateTime.UtcNow.AddSeconds(20),
            cancellationToken: cancellationToken);
        var values = new List<CloudDriveFileInfo>();
        await foreach (var reply in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var file in reply.SubFiles)
            {
                if (values.Count >= 10_000) throw new InvalidDataException("CloudDrive2 目录条目超过 10000 个。");
                values.Add(new CloudDriveFileInfo(file.Name, file.FullPathName, file.IsDirectory, file.Size));
            }
        }
        return values;
    }

    private static async Task AddOfflineFilesGrpcAsync(
        Uri endpoint,
        string token,
        IReadOnlyList<string> urls,
        string targetFolder,
        CancellationToken cancellationToken)
    {
        try
        {
            await AddOfflineFilesGrpcAttemptAsync(endpoint, $"Bearer {token}", urls, targetFolder, cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unauthenticated || error.Status.Detail.Contains("Invalid auth token", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await AddOfflineFilesGrpcAttemptAsync(endpoint, token, urls, targetFolder, cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException retryError)
            {
                throw new HttpRequestException($"CloudDrive2 离线下载提交失败 ({retryError.StatusCode})。", retryError);
            }
        }
        catch (RpcException error)
        {
            throw new HttpRequestException($"CloudDrive2 离线下载提交失败 ({error.StatusCode})。", error);
        }
    }

    private static async Task AddOfflineFilesGrpcAttemptAsync(
        Uri endpoint,
        string authorization,
        IReadOnlyList<string> urls,
        string targetFolder,
        CancellationToken cancellationToken)
    {
        using var channel = GrpcChannel.ForAddress(endpoint);
        var client = new CloudDriveFileSrv.CloudDriveFileSrvClient(channel);
        var headers = new Metadata { { "Authorization", authorization } };
        var response = await client.AddOfflineFilesAsync(
            new AddOfflineFileRequest
            {
                Urls = string.Join('\n', urls),
                ToFolder = targetFolder,
                CheckFolderAfterSecs = 30,
            },
            headers,
            deadline: DateTime.UtcNow.AddSeconds(20),
            cancellationToken: cancellationToken);
        if (!response.Success) throw new InvalidOperationException($"CloudDrive2 离线下载提交失败：{response.ErrorMessage.Trim()}");
    }

    private static Task MoveFilesGrpcAsync(
        Uri endpoint,
        string token,
        IReadOnlyList<string> paths,
        string destinationPath,
        CancellationToken cancellationToken) =>
        ExecuteWithTokenFallbackAsync(
            authorization => MoveFilesGrpcAttemptAsync(endpoint, authorization, paths, destinationPath, cancellationToken),
            token,
            "文件移动失败");

    private static async Task MoveFilesGrpcAttemptAsync(
        Uri endpoint,
        string authorization,
        IReadOnlyList<string> paths,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var channel = GrpcChannel.ForAddress(endpoint);
        var client = new CloudDriveFileSrv.CloudDriveFileSrvClient(channel);
        var request = new MoveFileRequest
        {
            DestPath = destinationPath,
            ConflictPolicy = MoveFileRequest.Types.ConflictPolicy.Rename,
            HandleConflictRecursively = true,
        };
        request.TheFilePaths.AddRange(paths);
        var response = await client.MoveFileAsync(
            request,
            new Metadata { { "Authorization", authorization } },
            deadline: DateTime.UtcNow.AddSeconds(30),
            cancellationToken: cancellationToken);
        if (!response.Success) throw new InvalidOperationException($"CloudDrive2 文件移动失败：{response.ErrorMessage.Trim()}");
    }

    private static Task CreateFolderGrpcAsync(
        Uri endpoint,
        string token,
        string parentPath,
        string folderName,
        CancellationToken cancellationToken) =>
        ExecuteWithTokenFallbackAsync(
            authorization => CreateFolderGrpcAttemptAsync(endpoint, authorization, parentPath, folderName, cancellationToken),
            token,
            "目录创建失败");

    private static async Task CreateFolderGrpcAttemptAsync(
        Uri endpoint,
        string authorization,
        string parentPath,
        string folderName,
        CancellationToken cancellationToken)
    {
        using var channel = GrpcChannel.ForAddress(endpoint);
        var client = new CloudDriveFileSrv.CloudDriveFileSrvClient(channel);
        var response = await client.CreateFolderAsync(
            new CreateFolderRequest { ParentPath = parentPath, FolderName = folderName },
            new Metadata { { "Authorization", authorization } },
            deadline: DateTime.UtcNow.AddSeconds(20),
            cancellationToken: cancellationToken);
        if (response.Result is null || !response.Result.Success)
            throw new InvalidOperationException($"CloudDrive2 目录创建失败：{response.Result?.ErrorMessage.Trim()}");
    }

    private static Task<string> UploadFileGrpcAsync(
        Uri endpoint,
        string token,
        ReadOnlyMemory<byte> content,
        string parentPath,
        string fileName,
        CancellationToken cancellationToken) =>
        ExecuteWithTokenFallbackAsync(
            authorization => UploadFileGrpcAttemptAsync(endpoint, authorization, content, parentPath, fileName, cancellationToken),
            token,
            "文件上传失败");

    private static async Task<string> UploadFileGrpcAttemptAsync(
        Uri endpoint,
        string authorization,
        ReadOnlyMemory<byte> content,
        string parentPath,
        string fileName,
        CancellationToken cancellationToken)
    {
        using var channel = GrpcChannel.ForAddress(endpoint);
        var client = new CloudDriveFileSrv.CloudDriveFileSrvClient(channel);
        var headers = new Metadata { { "Authorization", authorization } };
        var created = await client.CreateFileAsync(
            new CreateFileRequest { ParentPath = parentPath, FileName = fileName },
            headers,
            deadline: DateTime.UtcNow.AddSeconds(20),
            cancellationToken: cancellationToken);
        try
        {
            var response = await client.WriteToFileAsync(
                new WriteFileRequest
                {
                    FileHandle = created.FileHandle,
                    StartPos = 0,
                    Length = (ulong)content.Length,
                    Buffer = Google.Protobuf.ByteString.CopyFrom(content.Span),
                    CloseFile = true,
                },
                headers,
                deadline: DateTime.UtcNow.AddSeconds(60),
                cancellationToken: cancellationToken);
            if (response.BytesWritten != (ulong)content.Length) throw new InvalidOperationException("CloudDrive2 文件上传不完整。");
        }
        catch
        {
            try
            {
                await client.CloseFileAsync(
                    new CloseFileRequest { FileHandle = created.FileHandle },
                    headers,
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: CancellationToken.None);
            }
            catch (RpcException)
            {
            }
            throw;
        }
        return $"{parentPath.TrimEnd('/')}/{fileName}";
    }

    private static async Task ExecuteWithTokenFallbackAsync(Func<string, Task> operation, string token, string name)
    {
        await ExecuteWithTokenFallbackAsync(
            async authorization =>
            {
                await operation(authorization).ConfigureAwait(false);
                return true;
            },
            token,
            name).ConfigureAwait(false);
    }

    private static async Task<T> ExecuteWithTokenFallbackAsync<T>(Func<string, Task<T>> operation, string token, string name)
    {
        try
        {
            return await operation($"Bearer {token}").ConfigureAwait(false);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unauthenticated || error.Status.Detail.Contains("Invalid auth token", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return await operation(token).ConfigureAwait(false);
            }
            catch (RpcException retryError)
            {
                throw new HttpRequestException($"CloudDrive2 {name} ({retryError.StatusCode})。", retryError);
            }
        }
        catch (RpcException error)
        {
            throw new HttpRequestException($"CloudDrive2 {name} ({error.StatusCode})。", error);
        }
    }

    private static string NormalizeFileName(string value, string parameterName)
    {
        var name = value.Trim();
        if (name.Length is 0 or > 255 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("CloudDrive2 文件名无效。", parameterName);
        return name;
    }

    private static List<string> NormalizeOfflineUrls(IReadOnlyList<string> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);
        if (urls.Count is < 1 or > 100) throw new ArgumentException("离线下载 URL 数量必须为 1 到 100。", nameof(urls));
        var values = new List<string>(urls.Count);
        foreach (var raw in urls)
        {
            var value = raw.Trim();
            if (value.Length is 0 or > 4_096 || value.Contains('\r') || value.Contains('\n') ||
                !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("magnet" or "http" or "https") ||
                (uri.Scheme is "http" or "https") && !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("离线下载地址必须是无嵌入凭据的 magnet 或 HTTP(S) URL。", nameof(urls));
            values.Add(value);
        }
        return values;
    }

    private static string NormalizePath(string path)
    {
        var value = path.Trim().Replace('\\', '/');
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or "..")) throw new ArgumentException("CloudDrive2 目录不能包含路径遍历。", nameof(path));
        return segments.Length == 0 ? "/" : $"/{string.Join('/', segments)}";
    }

    internal static Uri ValidateEndpoint(string endpointUrl)
    {
        var endpoint = endpointUrl.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath is not ("" or "/") || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("CloudDrive2 地址格式不正确。", nameof(endpointUrl));
        return new Uri(uri.GetLeftPart(UriPartial.Authority));
    }
}
