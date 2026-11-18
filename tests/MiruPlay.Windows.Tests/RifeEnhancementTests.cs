using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class RifeEnhancementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "miruplay-rife-" + Guid.NewGuid().ToString("N"));

    public RifeEnhancementTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void EmbeddedManifestIsValidAndPinsExpectedComponents()
    {
        var manifest = RifeEnhancementManifest.LoadEmbedded();
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("rife-vulkan", manifest.Id);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Version));
        Assert.StartsWith("https://", manifest.SourceArchive.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, manifest.SourceArchive.Sha256.Length);
        Assert.NotEmpty(manifest.Files);
        var paths = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("VSScript.dll", paths);
        Assert.Contains("VSPipe.exe", paths);
        Assert.Contains("vs-plugins/librife_windows_x86-64.dll", paths);
        Assert.Contains("vs-plugins/models/rife-v4.6_ensembleFalse/flownet.bin", paths);
        Assert.Contains("licenses/VapourSynth-LGPL-2.1.txt", paths);
        Assert.Contains(manifest.Models!, model => model is { Id: "4.6", Default: true });
    }

    [Fact]
    public void RifeSettingsNormalizeClampsInvalidValues()
    {
        Assert.Equal(RifeEnhancementSettings.Default, RifeEnhancementSettings.Normalize(null));
        Assert.Equal(
            new RifeEnhancementSettings(2, RifeEnhancementSettings.Model46, 2, RifeEnhancementSettings.AutoProcessingHeight),
            RifeEnhancementSettings.Normalize(new RifeEnhancementSettings(9, "bogus", 9, "2160")));
        Assert.Equal(
            new RifeEnhancementSettings(3, RifeEnhancementSettings.Model425Lite, 4, "576"),
            RifeEnhancementSettings.Normalize(new RifeEnhancementSettings(3, "4.25-lite", 4, "576")));
        Assert.Equal(23, RifeEnhancementSettings.ToModelIndex("4.6"));
        Assert.Equal(70, RifeEnhancementSettings.ToModelIndex("4.25-lite"));
        Assert.Equal(480, RifeEnhancementSettings.ResolveProcessingHeight("auto", 480));
        Assert.Equal(720, RifeEnhancementSettings.ResolveProcessingHeight("720", 480));
    }

    [Fact]
    public void PlaybackScriptBuilderUsesForwardSlashesAndChosenParameters()
    {
        var plan = new RifePlaybackPlan(
            ScriptPath: Path.Combine(_directory, "active.vpy"),
            VSScriptPath: Path.Combine(_directory, "VSScript.dll"),
            RifePluginPath: Path.Combine(_directory, "vs-plugins", "librife_windows_x86-64.dll"),
            Factor: 3,
            Model: "4.25-lite",
            ModelIndex: 70,
            GpuThread: 4,
            ProcessingHeight: "576",
            MaxHeight: 576);

        var script = RifePlaybackScriptBuilder.BuildPlaybackScript(plan);
        // Portable VapourSynth autoloads vs-plugins (portable.vs), so generated scripts must
        // not call LoadPlugin again (a second load of the same plugin is rejected by VS).
        Assert.DoesNotContain("LoadPlugin", script);
        Assert.DoesNotContain("\\", script.Replace("\\n", string.Empty).Replace("\\\"", string.Empty));
        Assert.Contains("model=70", script);
        Assert.Contains("factor_num=3", script);
        Assert.Contains("gpu_thread=4", script);
        Assert.Contains("max_height = 576", script);
        Assert.Contains("set_output(0)", script);

        var argument = RifePlaybackScriptBuilder.BuildFilterArgument(plan.ScriptPath);
        Assert.StartsWith("vapoursynth=file=", argument);
        Assert.Contains("/active.vpy", argument);
        Assert.DoesNotContain("\\", argument);

        var selftest = RifePlaybackScriptBuilder.BuildSelfTestScript();
        Assert.Contains("BlankClip", selftest);
        Assert.Contains("model=23", selftest);
        Assert.Contains("factor_num=2", selftest);
    }

    [Fact]
    public void PlaybackRequestDegradesWhenPackMissing()
    {
        using var service = CreateService();
        var settings = new AppSettings(FrameInterpolationMode: "rife");
        var request = service.ResolvePlaybackRequest(settings, MpvDisplayPipeline.Auto);
        Assert.NotNull(request);
        Assert.Null(request!.Plan);
        Assert.Equal(RifeEnhancementReasons.NotInstalled, request.DegradedReasonKey);
        Assert.Null(service.ResolvePlaybackRequest(new AppSettings(), MpvDisplayPipeline.Auto));
    }

    [Fact]
    public void PlaybackRequestSkipsHdrPassthroughAndBuildsPlanOtherwise()
    {
        var packRoot = CreateInstalledPack("test-pack", out var manifest);
        var state = new RifeEnhancementStateStore(Path.Combine(_directory, "state.json"));
        state.Save(new RifeEnhancementState(InstalledVersion: "test-pack", SelfTest: new RifeSelfTestResult(
            true, 71.6, "Test GPU", null, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "test-pack", "480")));
        using var service = CreateService(packRoot, manifest, state);
        var settings = new AppSettings(FrameInterpolationMode: "rife");

        var hdr = service.ResolvePlaybackRequest(settings, MpvDisplayPipeline.HdrPassthrough);
        Assert.NotNull(hdr);
        Assert.Null(hdr!.Plan);
        Assert.Equal(RifeEnhancementReasons.HdrPassthrough, hdr.DegradedReasonKey);

        var planRequest = service.ResolvePlaybackRequest(settings, MpvDisplayPipeline.Auto);
        Assert.NotNull(planRequest);
        Assert.NotNull(planRequest!.Plan);
        var plan = planRequest.Plan!;
        Assert.Equal(23, plan.ModelIndex);
        Assert.Equal(2, plan.Factor);
        Assert.Equal(480, plan.MaxHeight);
        Assert.True(File.Exists(plan.ScriptPath));
        Assert.Contains("RIFE", File.ReadAllText(plan.ScriptPath));
        Assert.True(File.Exists(plan.VSScriptPath));
    }

    [Fact]
    public async Task InstallFromArchiveVerifiesFilesAndUninstalls()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VSScript.dll"] = "fake-vsscript",
            ["VSPipe.exe"] = "fake-vspipe",
            ["vs-plugins/librife_windows_x86-64.dll"] = "fake-rife",
            ["vs-plugins/models/rife-v4.6_ensembleFalse/flownet.bin"] = "fake-model",
            ["vs-plugins/models/rife-v4.6_ensembleFalse/flownet.param"] = "fake-param",
        };
        var manifest = BuildTestManifest(files, "install-pack", out var archivePath);
        using var service = CreateService(Path.Combine(_directory, "installed"), manifest,
            new RifeEnhancementStateStore(Path.Combine(_directory, "install-state.json")));

        var status = await service.InstallFromArchiveAsync(archivePath);
        Assert.True(status.Installed);
        Assert.Equal("install-pack", status.Version);
        Assert.True(File.Exists(Path.Combine(service.InstalledRoot, "VSScript.dll")));
        Assert.True(File.Exists(Path.Combine(service.InstalledRoot, "vs-plugins", "librife_windows_x86-64.dll")));

        var removed = await service.UninstallAsync();
        Assert.False(removed.Installed);
        Assert.False(Directory.Exists(service.InstalledRoot));

        var corrupt = Path.Combine(_directory, "corrupt.zip");
        File.Copy(archivePath, corrupt);
        using (var stream = new FileStream(corrupt, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 40;
            var value = stream.ReadByte();
            stream.Position = 40;
            stream.WriteByte((byte)(value ^ 0xFF));
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallFromArchiveAsync(corrupt));
        Assert.False(Directory.Exists(service.InstalledRoot));
    }

    [Fact]
    public async Task InstallRejectsUnexpectedExtraFiles()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VSScript.dll"] = "fake",
        };
        var manifest = BuildTestManifest(files, "extra-pack", out var archivePath);
        // Add an unexpected file after the manifest was computed.
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update))
        {
            var entry = zip.CreateEntry("malware.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("unexpected");
        }
        using var service = CreateService(Path.Combine(_directory, "extra"), manifest,
            new RifeEnhancementStateStore(Path.Combine(_directory, "extra-state.json")));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallFromArchiveAsync(archivePath));
        Assert.False(Directory.Exists(service.InstalledRoot));
    }

    private RifeEnhancementService CreateService(
        string? installedRoot = null,
        RifeEnhancementManifest? manifest = null,
        RifeEnhancementStateStore? state = null) =>
        new(
            installedRoot: installedRoot ?? Path.Combine(_directory, "installed"),
            bundledRoot: Path.Combine(_directory, "bundled"),
            stateStore: state ?? new RifeEnhancementStateStore(Path.Combine(_directory, "state.json")),
            httpClient: new HttpClient(),
            scriptDirectory: Path.Combine(_directory, "scripts"),
            mirrorRoot: Path.Combine(_directory, "mirror"),
            manifest: manifest);

    private string CreateInstalledPack(string version, out RifeEnhancementManifest manifest)
    {
        var root = Path.Combine(_directory, "pack-" + version);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "vs-plugins", "models", "rife-v4.6_ensembleFalse"));
        File.WriteAllText(Path.Combine(root, "VSScript.dll"), "vss");
        File.WriteAllText(Path.Combine(root, "VSPipe.exe"), "pipe");
        File.WriteAllText(Path.Combine(root, "vs-plugins", "librife_windows_x86-64.dll"), "rife");
        File.WriteAllText(Path.Combine(root, "vs-plugins", "models", "rife-v4.6_ensembleFalse", "flownet.bin"), "model");
        File.WriteAllText(Path.Combine(root, "vs-plugins", "models", "rife-v4.6_ensembleFalse", "flownet.param"), "param");
        File.WriteAllText(Path.Combine(root, "manifest.json"),
            JsonSerializer.Serialize(new { version, files = Array.Empty<object>() }));
        manifest = BuildTestManifest(new Dictionary<string, string>(), version, out _);
        return root;
    }

    private RifeEnhancementManifest BuildTestManifest(
        IReadOnlyDictionary<string, string> files,
        string version,
        out string archivePath)
    {
        archivePath = Path.Combine(_directory, "pack-" + version + ".zip");
        if (File.Exists(archivePath)) File.Delete(archivePath);
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var manifestEntry = zip.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
                writer.Write(JsonSerializer.Serialize(new { version, files = files.Keys }));

            foreach (var (relative, content) in files)
            {
                var entry = zip.CreateEntry(relative);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        var fileEntries = files.Select(item => new RifeEnhancementFile(
            item.Key,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Value))).ToLowerInvariant(),
            Encoding.UTF8.GetByteCount(item.Value))).ToArray();
        var archiveInfo = new FileInfo(archivePath);
        return new RifeEnhancementManifest(
            1,
            "rife-vulkan",
            version,
            "0.2.0",
            new RifeEnhancementArchive(
                "https://example.invalid/rife.zip",
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))).ToLowerInvariant(),
                archiveInfo.Length,
                "zip",
                fileEntries.Sum(file => file.Size),
                fileEntries.Length),
            fileEntries);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
