using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MiruPlay.Windows.Services;

public sealed record RifeEnhancementStatus(
    bool Installed,
    bool Ready,
    string? Version,
    string? Source,
    string? PackPath,
    long? PackBytes,
    string? ReasonKey,
    RifeSelfTestResult? SelfTest,
    IReadOnlyList<string> MissingFiles);

public static class RifeEnhancementReasons
{
    public const string NotInstalled = "rife.reason.notInstalled";
    public const string InvalidPack = "rife.reason.invalidPack";
    public const string SelftestRequired = "rife.reason.selftestRequired";
    public const string SelftestFailed = "rife.reason.selftestFailed";
    public const string SelftestSlow = "rife.reason.selftestSlow";
    public const string SelftestTimeout = "rife.reason.selftestTimeout";
    public const string SelftestExit = "rife.reason.selftestExit";
    public const string VulkanUnavailable = "rife.reason.vulkanUnavailable";
    public const string HdrPassthrough = "rife.reason.hdrPassthrough";
    public const string HdrContent = "rife.reason.hdrContent";
    public const string InitFailed = "rife.reason.initFailed";
    public const string Performance = "rife.reason.performance";
    public const string PathUnsupported = "rife.reason.pathUnsupported";
    public const string MirrorFailed = "rife.reason.mirrorFailed";
    public const string ArchiveUnsupported = "rife.reason.archiveUnsupported";
    public const string DownloadFailed = "rife.reason.downloadFailed";
    public const string InsufficientDisk = "rife.reason.insufficientDisk";
}

public static class RifeEnhancementStates
{
    public const string NotRequested = "not_requested";
    public const string Applied = "applied";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

/// <summary>
/// Owns the optional RIFE enhancement pack: installed/bundled detection, download + SHA-256 +
/// per-file verification, atomic install/rollback, VSPipe self-test and vpy generation for
/// libmpv playback. Network and filesystem dependencies are injectable for tests.
/// </summary>
public sealed class RifeEnhancementService : IDisposable
{
    private const long MaxArchiveBytes = 256L * 1024 * 1024;
    private const long MissingDiskHeadroomBytes = 128L * 1024 * 1024;
    private const double RequiredSelftestFps = 30.0;
    private const double SelftestHeadroomFactor = 1.1;
    private const double SelftestQualityFactor = 2.2;

    private static readonly Regex OutputFpsPattern = new(
        "Output\\s+(?<frames>[0-9]+)\\s+frames\\s+in\\s+(?<seconds>[0-9.]+)\\s+seconds\\s+\\((?<fps>[0-9.]+)\\s+fps\\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions AggregateManifestJsonOptions = new() { WriteIndented = true };
    private static readonly Regex GpuPattern = new(
        "^\\[(?<index>[0-9]+)\\s+(?<name>[^\\]]+)\\]",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly string _installedRoot;
    private readonly string _bundledRoot;
    private readonly string _scriptDirectory;
    private readonly string _mirrorRoot;
    private readonly RifeEnhancementStateStore _stateStore;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly RifeEnhancementManifest? _manifest;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private readonly SemaphoreSlim _selftestLock = new(1, 1);
    private RifeEnhancementStatus? _cachedStatus;
    private long _cachedStatusTicks;
    private int _disposed;

    public RifeEnhancementService(
        string? installedRoot = null,
        string? bundledRoot = null,
        RifeEnhancementStateStore? stateStore = null,
        HttpClient? httpClient = null,
        string? scriptDirectory = null,
        string? mirrorRoot = null,
        RifeEnhancementManifest? manifest = null)
    {
        _installedRoot = installedRoot ?? RifeEnhancementPaths.InstalledRoot;
        _bundledRoot = bundledRoot ?? RifeEnhancementPaths.BundledRoot;
        _stateStore = stateStore ?? new RifeEnhancementStateStore();
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _ownsHttpClient = httpClient is null;
        _scriptDirectory = scriptDirectory ?? RifeEnhancementPaths.ScriptDirectory;
        _mirrorRoot = mirrorRoot ?? RifeEnhancementPaths.MirrorRoot;
        try
        {
            _manifest = manifest ?? RifeEnhancementManifest.LoadEmbedded();
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or JsonException)
        {
            _manifest = null;
        }
    }

    public event EventHandler? StatusChanged;

    public RifeEnhancementManifest? Manifest => _manifest;

    public string InstalledRoot => _installedRoot;

    public IReadOnlyList<string> MirrorCandidates()
    {
        var candidates = new List<string>();
        if (RifeEnhancementPaths.IsAsciiPath(_mirrorRoot)) candidates.Add(_mirrorRoot);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(common) && RifeEnhancementPaths.IsAsciiPath(common))
            candidates.Add(Path.Combine(common, "MiruPlay", "rife-ascii"));
        var publicRoot = Environment.GetEnvironmentVariable("PUBLIC");
        if (!string.IsNullOrWhiteSpace(publicRoot) && RifeEnhancementPaths.IsAsciiPath(publicRoot))
            candidates.Add(Path.Combine(publicRoot, "MiruPlay", "rife-ascii"));
        var temp = Path.GetTempPath();
        if (RifeEnhancementPaths.IsAsciiPath(temp)) candidates.Add(Path.Combine(temp, "MiruPlay", "rife-ascii"));
        return candidates;
    }

    public bool IsSelfTestRunning => _selftestLock.CurrentCount == 0;

    public RifeEnhancementStatus GetStatus(bool refresh = false)
    {
        lock (_sync)
        {
            var now = Environment.TickCount64;
            if (!refresh && _cachedStatus is not null && now - _cachedStatusTicks < 2_000) return _cachedStatus;
            _cachedStatus = ComputeStatus();
            _cachedStatusTicks = now;
            return _cachedStatus;
        }
    }

    private RifeEnhancementStatus ComputeStatus()
    {
        var root = ResolvePackRoot(out var source);
        if (root is null)
        {
            var downloadedManifest = File.Exists(RifeEnhancementPaths.ManifestPath(_installedRoot));
            return new RifeEnhancementStatus(
                false, false, null, null, null, null,
                downloadedManifest ? RifeEnhancementReasons.InvalidPack : RifeEnhancementReasons.NotInstalled,
                null, []);
        }

        var version = TryReadPackVersion(root);
        var missing = RequiredFilesMissing(root);
        var installed = missing.Count == 0 && version is not null;
        var state = _stateStore.Load();
        var selftest = state.SelfTest is not null &&
            string.Equals(state.SelfTest.PackVersion, version, StringComparison.Ordinal)
                ? state.SelfTest
                : null;
        var ready = installed && selftest is { Ok: true };
        var reason = !installed
            ? RifeEnhancementReasons.InvalidPack
            : ready
                ? null
                : selftest is null
                    ? RifeEnhancementReasons.SelftestRequired
                    : selftest.ErrorKey ?? RifeEnhancementReasons.SelftestFailed;
        long? bytes = _manifest is not null && string.Equals(_manifest.Version, version, StringComparison.Ordinal)
            ? _manifest.SourceArchive.UnpackedSize
            : null;
        return new RifeEnhancementStatus(installed, ready, version, source, root, bytes, reason, selftest, missing);
    }

    private string? ResolvePackRoot(out string? source)
    {
        source = null;
        if (IsUsablePack(_installedRoot))
        {
            source = "installed";
            return _installedRoot;
        }
        if (IsUsablePack(_bundledRoot))
        {
            source = "bundled";
            return _bundledRoot;
        }
        return null;
    }

    private static bool IsUsablePack(string root) =>
        RifeEnhancementPaths.IsPackPresent(root) && TryReadPackVersion(root) is not null;

    private static string? TryReadPackVersion(string root)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(RifeEnhancementPaths.ManifestPath(root)));
            if (document.RootElement.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String)
            {
                var value = version.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static List<string> RequiredFilesMissing(string root)
    {
        var required = new[]
        {
            "VSScript.dll",
            "VSPipe.exe",
            "vs-plugins/" + RifeEnhancementPaths.RifePluginFileName,
            "vs-plugins/models/rife-v4.6_ensembleFalse/flownet.bin",
            "vs-plugins/models/rife-v4.6_ensembleFalse/flownet.param",
        };
        return required
            .Where(relative => !File.Exists(Path.Combine(root, relative.Replace('/', '\\'))))
            .ToList();
    }

    public async Task<RifeEnhancementStatus> InstallAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = GetStatus(refresh: true);
            if (status.Installed && status.Version is not null)
            {
                if (status.Ready) return status;
                await RunSelfTestAsync(cancellationToken).ConfigureAwait(false);
                return GetStatus(refresh: true);
            }
            var manifest = _manifest ?? throw new InvalidOperationException(RifeEnhancementReasons.InvalidPack);
            if (!string.Equals(manifest.SourceArchive.Format, "7z", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(manifest.SourceArchive.Format, "zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(RifeEnhancementReasons.ArchiveUnsupported);

            EnsureFreeSpace(manifest.SourceArchive.UnpackedSize);
            var extension = string.Equals(manifest.SourceArchive.Format, "zip", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".7z";
            var tempArchive = Path.Combine(Path.GetTempPath(), "miruplay-rife-" + Guid.NewGuid().ToString("N") + extension);
            try
            {
                await DownloadAsync(manifest, tempArchive, progress, cancellationToken).ConfigureAwait(false);
                await InstallFromArchiveCoreAsync(manifest, tempArchive, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try { File.Delete(tempArchive); } catch (IOException) { }
            }
            await RunSelfTestAsync(cancellationToken).ConfigureAwait(false);
            RaiseStatusChanged();
            return GetStatus(refresh: true);
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <summary>Install from a local release archive (offline install / tests).</summary>
    public async Task<RifeEnhancementStatus> InstallFromArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifest = _manifest ?? throw new InvalidOperationException(RifeEnhancementReasons.InvalidPack);
            EnsureFreeSpace(manifest.SourceArchive.UnpackedSize);
            await InstallFromArchiveCoreAsync(manifest, archivePath, cancellationToken).ConfigureAwait(false);
            await RunSelfTestAsync(cancellationToken).ConfigureAwait(false);
            RaiseStatusChanged();
            return GetStatus(refresh: true);
        }
        finally
        {
            _installLock.Release();
        }
    }

    private async Task DownloadAsync(
        RifeEnhancementManifest manifest,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient
                .GetAsync(manifest.SourceArchive.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            if (length is > MaxArchiveBytes) throw new InvalidDataException(RifeEnhancementReasons.DownloadFailed);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = File.Create(destination);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxArchiveBytes) throw new InvalidDataException(RifeEnhancementReasons.DownloadFailed);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (manifest.SourceArchive.Size > 0) progress?.Report(Math.Min(1d, total / (double)manifest.SourceArchive.Size));
            }
        }
        catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException)
        {
            throw new InvalidOperationException(RifeEnhancementReasons.DownloadFailed, error);
        }
    }

    private async Task InstallFromArchiveCoreAsync(
        RifeEnhancementManifest manifest,
        string archivePath,
        CancellationToken cancellationToken)
    {
        var archive = new FileInfo(archivePath);
        if (!archive.Exists) throw new InvalidDataException(RifeEnhancementReasons.DownloadFailed);
        if (manifest.SourceArchive.Size > 0 && archive.Length != manifest.SourceArchive.Size)
            throw new InvalidDataException(RifeEnhancementReasons.DownloadFailed);
        if (!string.Equals(await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false),
                manifest.SourceArchive.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(RifeEnhancementReasons.DownloadFailed);

        var parent = Path.GetDirectoryName(_installedRoot) ?? Path.GetTempPath();
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".rife-staging-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(parent, ".rife-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            ExtractArchive(archivePath, staging, manifest.SourceArchive.Format);
            await VerifyFilesAsync(staging, manifest, cancellationToken).ConfigureAwait(false);
            var installedManifest = BuildAggregateManifest(manifest);
            File.WriteAllText(RifeEnhancementPaths.ManifestPath(staging), installedManifest, new UTF8Encoding(false));

            if (Directory.Exists(_installedRoot)) Directory.Move(_installedRoot, backup);
            Directory.Move(staging, _installedRoot);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
        }
        catch
        {
            if (Directory.Exists(backup) && !Directory.Exists(_installedRoot)) Directory.Move(backup, _installedRoot);
            throw;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
        }
    }

    private static void ExtractArchive(string archivePath, string staging, string? format)
    {
        if (string.Equals(format, "zip", StringComparison.OrdinalIgnoreCase))
        {
            ExtractZipArchive(archivePath, staging);
            return;
        }
        ExtractSevenZipArchive(archivePath, staging);
    }

    private static void ExtractZipArchive(string archivePath, string staging)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var relative = entry.FullName.Replace('\\', '/');
            if (!RifeEnhancementManifest.IsSafeRelativePath(relative))
                throw new InvalidDataException(RifeEnhancementReasons.ArchiveUnsupported);
            var target = Path.Combine(staging, relative.Replace('/', '\\'));
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            entry.ExtractToFile(target, true);
        }
        if (!File.Exists(RifeEnhancementPaths.ManifestPath(staging)))
            throw new InvalidDataException(RifeEnhancementReasons.ArchiveUnsupported);
    }

    /// <summary>7z archives are extracted with the Windows bundled bsdtar (tar.exe, Windows 10
    /// 1803+). Entries are validated before extraction to reject zip-slip style paths.</summary>
    private static void ExtractSevenZipArchive(string archivePath, string staging)
    {
        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(tar)) tar = "tar.exe";
        var listing = RunArchiveTool(tar, ["-tf", archivePath], archivePath);
        foreach (var line in listing.Split('\n'))
        {
            var relative = line.Trim().TrimEnd('\r').Replace('\\', '/');
            if (relative.Length == 0 || relative.EndsWith('/')) continue;
            if (!RifeEnhancementManifest.IsSafeRelativePath(relative))
                throw new InvalidDataException(RifeEnhancementReasons.ArchiveUnsupported);
        }
        RunArchiveTool(tar, ["-xf", archivePath, "-C", staging], archivePath);
        if (!File.Exists(RifeEnhancementPaths.ManifestPath(staging)))
            throw new InvalidDataException(RifeEnhancementReasons.ArchiveUnsupported);
    }

    private static string RunArchiveTool(string tool, string[] arguments, string archivePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = tool,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidDataException(RifeEnhancementReasons.ArchiveUnsupported);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(120_000) || process.ExitCode != 0)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(error) ? RifeEnhancementReasons.ArchiveUnsupported : error.Trim());
        }
        return output;
    }

    private static async Task VerifyFilesAsync(
        string staging,
        RifeEnhancementManifest manifest,
        CancellationToken cancellationToken)
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var target = Path.Combine(staging, file.Path.Replace('/', '\\'));
            expected.Add(file.Path.Replace('\\', '/'));
            if (!File.Exists(target)) throw new InvalidDataException(RifeEnhancementReasons.InvalidPack);
            if (file.Size > 0 && new FileInfo(target).Length != file.Size)
                throw new InvalidDataException(RifeEnhancementReasons.InvalidPack);
            if (!string.Equals(await ComputeSha256Async(target, cancellationToken).ConfigureAwait(false),
                    file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(RifeEnhancementReasons.InvalidPack);
        }
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, file).Replace('\\', '/');
            if (relative.Equals(RifeEnhancementPaths.ManifestFileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!expected.Contains(relative)) throw new InvalidDataException(RifeEnhancementReasons.InvalidPack);
        }
    }

    private static string BuildAggregateManifest(RifeEnhancementManifest manifest)
    {
        var payload = new
        {
            version = manifest.Version,
            files = manifest.Files.Select(file => new { path = file.Path, sha256 = file.Sha256 }).ToArray(),
        };
        return JsonSerializer.Serialize(payload, AggregateManifestJsonOptions);
    }

    private void EnsureFreeSpace(long unpackedBytes)
    {
        if (unpackedBytes <= 0) return;
        try
        {
            var root = Path.GetPathRoot(_installedRoot) ?? "C:\\";
            var drive = new DriveInfo(root);
            var needed = (long)(unpackedBytes * 1.2) + MissingDiskHeadroomBytes;
            if (drive.AvailableFreeSpace < needed)
                throw new InvalidOperationException(RifeEnhancementReasons.InsufficientDisk);
        }
        catch (Exception error) when (error is ArgumentException or IOException)
        {
        }
    }

    public async Task<RifeSelfTestResult> RunSelfTestAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _selftestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = GetStatus(refresh: true);
            if (!status.Installed || status.PackPath is null)
            {
                var unavailable = new RifeSelfTestResult(
                    false, null, null, RifeEnhancementReasons.NotInstalled, null,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), status.Version, null);
                SaveSelfTest(unavailable);
                return unavailable;
            }

            var effectiveRoot = ResolveEffectiveRuntimeRoot(status.PackPath, status.Version ?? "unknown");
            if (effectiveRoot is null)
            {
                var mirrorFailure = new RifeSelfTestResult(
                    false, null, null, RifeEnhancementReasons.MirrorFailed, null,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), status.Version, null);
                SaveSelfTest(mirrorFailure);
                return mirrorFailure;
            }

            var scriptDirectory = ResolveScriptDirectory(effectiveRoot);
            Directory.CreateDirectory(scriptDirectory);
            var scriptPath = Path.Combine(scriptDirectory, "selftest-480p.vpy");
            await File.WriteAllTextAsync(
                scriptPath,
                RifePlaybackScriptBuilder.BuildSelfTestScript(),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            var result = await RunVSPipeSelfTestAsync(effectiveRoot, scriptPath, status.Version, cancellationToken)
                .ConfigureAwait(false);
            SaveSelfTest(result);
            RaiseStatusChanged();
            return result;
        }
        finally
        {
            _selftestLock.Release();
        }
    }

    private static async Task<RifeSelfTestResult> RunVSPipeSelfTestAsync(
        string packRoot,
        string scriptPath,
        string? packVersion,
        CancellationToken cancellationToken)
    {
        var evaluatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pipe = RifeEnhancementPaths.VSPipePath(packRoot);
        if (!File.Exists(pipe))
            return new(false, null, null, RifeEnhancementReasons.InvalidPack, null, evaluatedAt, packVersion, null);

        var startInfo = new ProcessStartInfo
        {
            FileName = pipe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("y4m");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("NUL");

        using var process = new Process { StartInfo = startInfo };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var output = new StringBuilder();
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) lock (output) output.AppendLine(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) lock (output) output.AppendLine(args.Data); };
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            return new(false, null, null, RifeEnhancementReasons.SelftestTimeout, null, evaluatedAt, packVersion, null);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(false, null, null, RifeEnhancementReasons.SelftestExit, Trim(error.Message), evaluatedAt, packVersion, null);
        }

        string text;
        lock (output) text = output.ToString();
        if (process.ExitCode != 0)
            return new(false, null, null, RifeEnhancementReasons.SelftestExit, Trim(text), evaluatedAt, packVersion, null);

        var match = OutputFpsPattern.Match(text);
        if (!match.Success)
        {
            var vulkan = text.Contains("vulkan", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("gpu", StringComparison.OrdinalIgnoreCase);
            var reason = vulkan ? RifeEnhancementReasons.VulkanUnavailable : RifeEnhancementReasons.SelftestExit;
            return new(false, null, null, reason, Trim(text), evaluatedAt, packVersion, null);
        }

        var fps = double.Parse(match.Groups["fps"].Value, CultureInfo.InvariantCulture);
        var gpuMatch = GpuPattern.Match(text);
        var gpu = gpuMatch.Success ? gpuMatch.Groups["name"].Value.Trim() : null;
        if (fps < RequiredSelftestFps)
            return new(false, fps, gpu, RifeEnhancementReasons.SelftestSlow, Trim(text), evaluatedAt, packVersion, null);

        var recommended = RecommendProcessingHeight(fps);
        return new(true, fps, gpu, null, null, evaluatedAt, packVersion, recommended);
    }

    private static string RecommendProcessingHeight(double fps480p)
    {
        var candidates = new[] { 1080, 720, 576, 540, 480, 360 };
        var target = 48.0 * SelftestHeadroomFactor;
        foreach (var height in candidates)
        {
            var estimate = fps480p * Math.Pow(480d / height, 2d) * 0.9;
            if (estimate >= target) return height.ToString(CultureInfo.InvariantCulture);
        }
        return "360";
    }

    private static string? Trim(string value) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= 2000 ? value : value[..2000];

    private void SaveSelfTest(RifeSelfTestResult result)
    {
        var state = _stateStore.Load();
        _stateStore.Save(state with { SelfTest = result });
        _cachedStatus = null;
    }

    public Task<RifeEnhancementStatus> UninstallAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(_installedRoot)) Directory.Delete(_installedRoot, true);
        var state = _stateStore.Load();
        _stateStore.Save(state with { InstalledVersion = null, InstalledAtUtc = 0, SelfTest = null });
        try
        {
            if (Directory.Exists(_mirrorRoot)) Directory.Delete(_mirrorRoot, true);
            if (Directory.Exists(_scriptDirectory)) Directory.Delete(_scriptDirectory, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        RaiseStatusChanged();
        return Task.FromResult(GetStatus(refresh: true));
    }

    /// <summary>Decides whether a RIFE session can run and materialises its vpy script.</summary>
    public RifePlaybackRequest? ResolvePlaybackRequest(AppSettings settings, MpvDisplayPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.FrameInterpolationMode != MpvFrameInterpolationModes.Rife) return null;
        var status = GetStatus();
        if (!status.Installed || status.PackPath is null)
            return new RifePlaybackRequest(null, RifeEnhancementReasons.NotInstalled);
        if (status.SelfTest is not { Ok: true })
            return new RifePlaybackRequest(null, RifeEnhancementReasons.SelftestRequired);
        if (pipeline == MpvDisplayPipeline.HdrPassthrough)
            return new RifePlaybackRequest(null, RifeEnhancementReasons.HdrPassthrough);

        var effectiveRoot = ResolveEffectiveRuntimeRoot(status.PackPath, status.Version ?? "unknown");
        if (effectiveRoot is null)
            return new RifePlaybackRequest(null, RifeEnhancementReasons.MirrorFailed);

        var rife = RifeEnhancementSettings.Normalize(settings.RifeEnhancement);
        var recommended = int.TryParse(status.SelfTest.RecommendedHeight, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            ? height
            : (int?)null;
        var maxHeight = RifeEnhancementSettings.ResolveProcessingHeight(rife.ProcessingHeight, recommended);
        var modelIndex = RifeEnhancementSettings.ToModelIndex(rife.Model);
        var plan = new RifePlaybackPlan(
            ScriptPath: string.Empty,
            VSScriptPath: RifeEnhancementPaths.VSScriptPath(effectiveRoot),
            RifePluginPath: RifeEnhancementPaths.RifePluginPath(effectiveRoot),
            Factor: rife.Factor,
            Model: rife.Model,
            ModelIndex: modelIndex,
            GpuThread: rife.GpuThread,
            ProcessingHeight: maxHeight.ToString(CultureInfo.InvariantCulture),
            MaxHeight: maxHeight);

        try
        {
            var scriptPath = EnsurePlaybackScript(plan, effectiveRoot);
            return new RifePlaybackRequest(plan with { ScriptPath = scriptPath }, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new RifePlaybackRequest(null, RifeEnhancementReasons.InitFailed);
        }
    }

    private string EnsurePlaybackScript(RifePlaybackPlan plan, string effectiveRoot)
    {
        var scriptDirectory = ResolveScriptDirectory(effectiveRoot);
        Directory.CreateDirectory(scriptDirectory);
        var key = string.Join("|", plan.ModelIndex, plan.Factor, plan.GpuThread, plan.MaxHeight, plan.RifePluginPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
        var path = Path.Combine(scriptDirectory, "active-" + hash + ".vpy");
        var content = RifePlaybackScriptBuilder.BuildPlaybackScript(plan);
        if (!File.Exists(path) || !string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
            File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>VapourSynth/RIFE cannot load plugin models from non-ASCII paths. When the
    /// pack lives under a non-ASCII profile path, mirror the whole runtime to an ASCII-only
    /// location so portable plugin autoloading (portable.vs + vs-plugins) keeps working.</summary>
    private string? ResolveEffectiveRuntimeRoot(string packRoot, string version)
    {
        if (RifeEnhancementPaths.IsAsciiPath(packRoot))
            return packRoot;

        foreach (var candidate in MirrorCandidates())
        {
            try
            {
                var root = Path.Combine(candidate, Sanitize(version));
                var marker = Path.Combine(root, ".mirror-ok");
                if (!File.Exists(marker) || !File.Exists(RifeEnhancementPaths.VSScriptPath(root)))
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                    Directory.CreateDirectory(root);
                    CopyDirectory(packRoot, root);
                    File.WriteAllText(marker, version, new UTF8Encoding(false));
                }
                if (File.Exists(RifeEnhancementPaths.VSScriptPath(root))) return root;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
        return null;
    }

    private string ResolveScriptDirectory(string effectiveRoot)
    {
        if (RifeEnhancementPaths.IsAsciiPath(_scriptDirectory)) return _scriptDirectory;
        return Path.Combine(effectiveRoot, "scripts");
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(invalid.Contains(character) ? '_' : character);
        return builder.ToString();
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Copy(file, target, true);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void RaiseStatusChanged()
    {
        lock (_sync) _cachedStatus = null;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _installLock.Dispose();
        _selftestLock.Dispose();
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}