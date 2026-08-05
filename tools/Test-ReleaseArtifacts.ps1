[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ReleaseDirectory,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$release = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReleaseDirectory)
$manifestPath = Join-Path $release 'release-manifest.json'
$checksumsPath = Join-Path $release 'SHA256SUMS.txt'
if (-not (Test-Path $manifestPath) -or -not (Test-Path $checksumsPath)) {
    throw 'release-manifest.json and SHA256SUMS.txt are required.'
}
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$portable = Get-ChildItem $release -Filter "MiruPlay-$($manifest.appVersion)-$($manifest.runtimeIdentifier)-portable.zip" -File | Select-Object -First 1
$setup = Get-ChildItem $release -Filter "MiruPlay-$($manifest.appVersion)-$($manifest.runtimeIdentifier)-setup.exe" -File | Select-Object -First 1
if (-not $portable) { throw 'Portable release archive was not found.' }
if (-not $SkipInstaller -and -not $setup) { throw 'Installer release artifact was not found.' }

$verifiedHashes = 0
foreach ($line in Get-Content $checksumsPath) {
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw "Invalid checksum line: $line" }
    $path = Join-Path $release $Matches[2]
    if (-not (Test-Path $path)) { throw "Checksummed artifact is missing: $($Matches[2])" }
    $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Matches[1]) { throw "SHA-256 mismatch for $($Matches[2])" }
    $verifiedHashes++
}

function Test-AppLaunch {
    param([string]$Executable)
    $process = Start-Process -FilePath $Executable -WorkingDirectory (Split-Path $Executable) -PassThru
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while (-not $process.HasExited -and $process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 250
            $process.Refresh()
        }
        if ($process.HasExited) { throw "MiruPlay exited during launch with code $($process.ExitCode)." }
        if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'MiruPlay did not create a main window within 20 seconds.' }
    }
    finally {
        if (-not $process.HasExited) {
            $process.CloseMainWindow() | Out-Null
            if (-not $process.WaitForExit(20000)) {
                $process.Kill()
                throw 'MiruPlay did not exit cleanly after its window was closed.'
            }
        }
        $process.Dispose()
    }
}

$temp = Join-Path ([System.IO.Path]::GetTempPath()) "miruplay-release-$([Guid]::NewGuid().ToString('N'))"
$portableDirectory = Join-Path $temp 'portable'
$installDirectory = Join-Path $temp 'installed'
New-Item -ItemType Directory -Force -Path $portableDirectory | Out-Null
try {
    Expand-Archive $portable.FullName $portableDirectory
    foreach ($required in @(
        'MiruPlay.exe',
        'MiruPlay.dll',
        'LICENSE',
        'README.md',
        'THIRD-PARTY-NOTICES.md',
        'release-manifest.json',
        'runtime\libmpv\libmpv-2.dll',
        'licenses\NuGet-Packages.json',
        'licenses\Apache-2.0.txt',
        'licenses\mpv-Copyright.txt'
    )) {
        if (-not (Test-Path (Join-Path $portableDirectory $required))) { throw "Portable package is missing $required" }
    }
    $portableMpvFiles = @(Get-ChildItem $portableDirectory -Filter 'mpv.exe' -File -Recurse)
    if ((Test-Path (Join-Path $portableDirectory 'runtime\mpv')) -or $portableMpvFiles.Count -gt 0) {
        throw 'Portable package contains the removed external mpv runtime.'
    }
    Test-AppLaunch (Join-Path $portableDirectory 'MiruPlay.exe')

    $installerTested = $false
    if (-not $SkipInstaller) {
        $installer = Start-Process -FilePath $setup.FullName -ArgumentList @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/SP-',
            "/DIR=`"$installDirectory`""
        ) -Wait -PassThru
        if ($installer.ExitCode -ne 0) { throw "Installer exited with code $($installer.ExitCode)." }
        $installedExecutable = Join-Path $installDirectory 'MiruPlay.exe'
        if (-not (Test-Path $installedExecutable)) { throw 'Silent installation did not install MiruPlay.exe.' }
        if (-not (Test-Path (Join-Path $installDirectory 'runtime\libmpv\libmpv-2.dll'))) { throw 'Silent installation did not install the libmpv runtime.' }
        $installedMpvFiles = @(Get-ChildItem $installDirectory -Filter 'mpv.exe' -File -Recurse)
        if ((Test-Path (Join-Path $installDirectory 'runtime\mpv')) -or $installedMpvFiles.Count -gt 0) {
            throw 'Installed package contains the removed external mpv runtime.'
        }
        if (-not (Test-Path (Join-Path $installDirectory 'licenses\NuGet-Packages.json'))) { throw 'Silent installation did not install third-party license metadata.' }
        Test-AppLaunch $installedExecutable
        $uninstallerPath = Join-Path $installDirectory 'unins000.exe'
        if (-not (Test-Path $uninstallerPath)) { throw 'Installer did not create an uninstaller.' }
        $uninstaller = Start-Process -FilePath $uninstallerPath -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
        if ($uninstaller.ExitCode -ne 0) { throw "Uninstaller exited with code $($uninstaller.ExitCode)." }
        if (Test-Path $installedExecutable) { throw 'Uninstall left MiruPlay.exe in the installation directory.' }
        $installerTested = $true
    }

    [pscustomobject]@{
        Version = $manifest.appVersion
        RuntimeIdentifier = $manifest.runtimeIdentifier
        Signing = $manifest.signing
        VerifiedHashes = $verifiedHashes
        PortableLaunch = $true
        InstallerTested = $installerTested
        InstallerUninstall = $installerTested
    } | ConvertTo-Json
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
