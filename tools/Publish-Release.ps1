[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0',
    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$ArtifactsDirectory,
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$SkipInstaller,
    [switch]$SkipMpvDownload
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) { $ArtifactsDirectory = Join-Path $root 'artifacts' }
$artifacts = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
$releaseDirectory = Join-Path $artifacts 'release'
$publishDirectory = Join-Path $artifacts "publish\$RuntimeIdentifier\MiruPlay"
$symbolsDirectory = Join-Path $artifacts "symbols\$RuntimeIdentifier"
$mpvDirectory = Join-Path $root 'runtime\mpv'
$mpvPath = Join-Path $mpvDirectory 'mpv.exe'
$project = Join-Path $root 'src\MiruPlay.Windows\MiruPlay.Windows.csproj'
$installerScript = Join-Path $root 'installer\MiruPlay.iss'

function Invoke-Checked {
    param([scriptblock]$Command, [string]$FailureMessage)
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$FailureMessage (exit code $LASTEXITCODE)" }
}

function Copy-ThirdPartyLicenses {
    param([string]$ProjectAssetsPath, [string]$Destination)

    $assets = Get-Content $ProjectAssetsPath -Raw | ConvertFrom-Json
    $packageRoot = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
    $targetName = $assets.targets.PSObject.Properties.Name |
        Where-Object { $_ -like "*/$RuntimeIdentifier" } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($targetName)) {
        throw "NuGet assets do not contain a $RuntimeIdentifier target."
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item (Join-Path $root 'licenses\*') $Destination -Recurse -Force

    $packageIndex = @()
    $target = $assets.targets.PSObject.Properties[$targetName].Value
    foreach ($property in $target.PSObject.Properties) {
        if ($property.Value.type -ne 'package') { continue }

        $separator = $property.Name.LastIndexOf('/')
        $packageId = $property.Name.Substring(0, $separator)
        $packageVersion = $property.Name.Substring($separator + 1)
        $packageDirectory = Join-Path $packageRoot (Join-Path $packageId.ToLowerInvariant() $packageVersion)
        $nuspec = Get-ChildItem $packageDirectory -Filter '*.nuspec' | Select-Object -First 1
        $nuspecText = Get-Content $nuspec.FullName -Raw
        $license = if ($nuspecText -match '<license[^>]*>([^<]+)</license>') { $Matches[1] } else { 'See packaged metadata' }
        $repository = if ($nuspecText -match '<repository[^>]*url="([^"]+)"') { $Matches[1] } else { $null }
        foreach ($licenseFile in @(Get-ChildItem $packageDirectory -File | Where-Object {
            $_.Name -match '^(LICENSE|NOTICE|THIRD-PARTY-NOTICES)'
        })) {
            Copy-Item $licenseFile.FullName (Join-Path $Destination "$packageId-$packageVersion-$($licenseFile.Name)") -Force
        }
        $packageIndex += [ordered]@{
            id = $packageId
            version = $packageVersion
            license = $license
            repository = $repository
        }
    }

    $framework = $assets.project.frameworks.PSObject.Properties.Value | Select-Object -First 1
    foreach ($dependency in @($framework.downloadDependencies)) {
        $runtimeVersion = ([string]$dependency.version).Trim('[', ']').Split(',')[0]
        $runtimeDirectory = Join-Path $packageRoot (Join-Path $dependency.name.ToLowerInvariant() $runtimeVersion)
        foreach ($licenseFile in @(Get-ChildItem $runtimeDirectory -File | Where-Object {
            $_.Name -match '^(LICENSE|NOTICE|THIRD-PARTY-NOTICES)'
        })) {
            Copy-Item $licenseFile.FullName (Join-Path $Destination "$($dependency.name)-$runtimeVersion-$($licenseFile.Name)") -Force
        }
        $packageIndex += [ordered]@{
            id = $dependency.name
            version = $runtimeVersion
            license = 'See packaged runtime license and third-party notices'
            repository = 'https://github.com/dotnet/dotnet'
        }
    }

    [IO.File]::WriteAllText(
        (Join-Path $Destination 'NuGet-Packages.json'),
        ($packageIndex | ConvertTo-Json -Depth 4),
        [Text.UTF8Encoding]::new($false))
}

function Find-SignTool {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $tool = Get-ChildItem -Path $kits -Filter signtool.exe -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $tool) { throw 'signtool.exe was not found in the Windows SDK.' }
    return $tool.FullName
}

function Sign-File {
    param([string]$Path, [string]$SignTool)
    $arguments = @('sign', '/fd', 'SHA256')
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $arguments += @('/td', 'SHA256', '/tr', $TimestampUrl)
    }
    $arguments += @('/f', $CertificatePath)
    if (-not [string]::IsNullOrEmpty($CertificatePassword)) {
        $arguments += @('/p', $CertificatePassword)
    }
    $arguments += $Path
    & $SignTool @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for $Path" }
    & $SignTool verify /pa /v $Path | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed for $Path" }
}

function Find-Iscc {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $match = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $match) { throw 'Inno Setup 6 is required. Install JRSoftware.InnoSetup or use -SkipInstaller.' }
    return $match
}

if (-not (Test-Path $mpvPath)) {
    if ($SkipMpvDownload) { throw "mpv runtime is missing: $mpvPath" }
    Invoke-Checked { & (Join-Path $PSScriptRoot 'Get-MpvRuntime.ps1') -Destination $mpvDirectory } 'mpv runtime download failed'
}
if (-not (Test-Path (Join-Path $mpvDirectory 'd3dcompiler_43.dll'))) {
    throw 'The pinned mpv runtime is incomplete: d3dcompiler_43.dll is missing.'
}

Remove-Item $publishDirectory, $symbolsDirectory, $releaseDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publishDirectory, $symbolsDirectory, $releaseDirectory | Out-Null

Invoke-Checked {
    dotnet publish $project -c Release -r $RuntimeIdentifier --self-contained true -p:Version=$Version -p:PublishTrimmed=false -o $publishDirectory
} 'dotnet publish failed'

foreach ($file in @('LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item (Join-Path $root $file) $publishDirectory
}
Copy-ThirdPartyLicenses `
    (Join-Path $root 'src\MiruPlay.Windows\obj\project.assets.json') `
    (Join-Path $publishDirectory 'licenses')

$signed = -not [string]::IsNullOrWhiteSpace($CertificatePath)
$signTool = $null
if ($signed) {
    if (-not (Test-Path $CertificatePath)) { throw "Signing certificate was not found: $CertificatePath" }
    $signTool = Find-SignTool
    Sign-File (Join-Path $publishDirectory 'MiruPlay.exe') $signTool
    Sign-File (Join-Path $publishDirectory 'MiruPlay.dll') $signTool
}

$mpvVersion = ((& $mpvPath --version | Select-Object -First 1) -split ' Copyright', 2)[0].Trim()
$sourceRevision = if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_SHA)) {
    $env:GITHUB_SHA
} elseif (-not (Test-Path (Join-Path $root '.git'))) {
    'source-export'
} else {
    $gitStatus = @(& git -C $root status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw 'Git source revision inspection failed.'
    } elseif ($gitStatus.Count -gt 0) {
        'working-tree'
    } else {
        $revision = & git -C $root rev-parse HEAD
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($revision)) {
            $revision.Trim()
        } else {
            'source-export'
        }
    }
}
$global:LASTEXITCODE = 0
$manifest = [ordered]@{
    schemaVersion = 1
    appVersion = $Version
    runtimeIdentifier = $RuntimeIdentifier
    deployment = 'self-contained'
    targetFramework = 'net10.0-windows'
    dotnetSdk = (dotnet --version).Trim()
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    sourceRevision = $sourceRevision
    signing = if ($signed) { 'authenticode' } else { 'unsigned' }
    mpv = [ordered]@{
        version = $mpvVersion
        executableSha256 = (Get-FileHash $mpvPath -Algorithm SHA256).Hash.ToLowerInvariant()
        distribution = 'shinchiro/mpv-winbuild-cmake'
    }
}
$manifestPath = Join-Path $releaseDirectory 'release-manifest.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Copy-Item $manifestPath $publishDirectory

$symbolFiles = Get-ChildItem $publishDirectory -Filter *.pdb -File -Recurse
$symbolsArchive = Join-Path $releaseDirectory "MiruPlay-$Version-$RuntimeIdentifier-symbols.zip"
if ($symbolFiles.Count -gt 0) {
    foreach ($symbol in $symbolFiles) { Copy-Item $symbol.FullName $symbolsDirectory }
    Compress-Archive -Path (Join-Path $symbolsDirectory '*') -DestinationPath $symbolsArchive -CompressionLevel Optimal
    $symbolFiles | Remove-Item -Force
}

$portableArchive = Join-Path $releaseDirectory "MiruPlay-$Version-$RuntimeIdentifier-portable.zip"
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $portableArchive -CompressionLevel Optimal

if (-not $SkipInstaller) {
    $iscc = Find-Iscc
    $versionMatch = [regex]::Match($Version, '^(\d+)\.(\d+)\.(\d+)')
    $numericVersion = "$($versionMatch.Groups[1].Value).$($versionMatch.Groups[2].Value).$($versionMatch.Groups[3].Value).0"
    $setupBaseName = "MiruPlay-$Version-$RuntimeIdentifier-setup"
    & $iscc /Qp "/DSourceDir=$publishDirectory" "/DAppVersion=$Version" "/DVersionNumeric=$numericVersion" "/DOutputDir=$releaseDirectory" "/DOutputBaseFilename=$setupBaseName" $installerScript | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed (exit code $LASTEXITCODE)" }
    $setupPath = Join-Path $releaseDirectory "$setupBaseName.exe"
    if ($signed) { Sign-File $setupPath $signTool }
}

$checksumPath = Join-Path $releaseDirectory 'SHA256SUMS.txt'
Get-ChildItem $releaseDirectory -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object Name |
    ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
    Set-Content $checksumPath -Encoding ascii

Write-Output "Release artifacts: $releaseDirectory"
Get-ChildItem $releaseDirectory -File | Sort-Object Name | Select-Object Name, Length
