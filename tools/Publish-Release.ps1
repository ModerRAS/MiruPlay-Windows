[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0',
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$GitHubRepository = 'ModerRAS/MiruPlay-Windows',
    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [ValidateSet('lite', 'full')]
    [string]$Edition = 'lite',
    [string]$ArtifactsDirectory,
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$SkipInstaller,
    [switch]$SkipLibMpvDownload,
    [switch]$SkipSdl3Download
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) { $ArtifactsDirectory = Join-Path $root 'artifacts' }
$artifacts = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
$releaseDirectory = Join-Path $artifacts 'release'
$publishDirectory = Join-Path $artifacts "publish\$RuntimeIdentifier\$Edition\MiruPlay"
$symbolsDirectory = Join-Path $artifacts "symbols\$RuntimeIdentifier\$Edition"
$libMpvDirectory = Join-Path $root 'runtime\libmpv'
$libMpvPath = Join-Path $libMpvDirectory 'libmpv-2.dll'
$sdl3Directory = Join-Path $root 'runtime\sdl3'
$sdl3Path = Join-Path $sdl3Directory 'SDL3.dll'
$enhanceDirectory = Join-Path $root 'runtime\enhance'
$enhanceManifestPath = Join-Path $enhanceDirectory 'manifest.json'
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

if (-not (Test-Path $libMpvPath)) {
    if ($SkipLibMpvDownload) { throw "libmpv runtime is missing: $libMpvPath" }
    Invoke-Checked { & (Join-Path $PSScriptRoot 'Get-LibMpvRuntime.ps1') -Destination $libMpvDirectory } 'libmpv runtime download failed'
}
$expectedLibMpvHash = '88504ee5560942a955405e99dfb5971368e4cf851321f36565bf2a667271db1b'
$actualLibMpvHash = (Get-FileHash -Algorithm SHA256 $libMpvPath).Hash.ToLowerInvariant()
if ($actualLibMpvHash -ne $expectedLibMpvHash) {
    throw "libmpv runtime digest mismatch: $actualLibMpvHash"
}

if (-not (Test-Path $sdl3Path)) {
    if ($SkipSdl3Download) { throw "SDL3 runtime is missing: $sdl3Path" }
    Invoke-Checked { & (Join-Path $PSScriptRoot 'Get-Sdl3Runtime.ps1') -Destination $sdl3Directory } 'SDL3 runtime download failed'
}
$expectedSdl3Hash = '66844f10251cc1c10619ed5d8e4e3224f564900ee6d69ba87d63de782addae40'
$actualSdl3Hash = (Get-FileHash -Algorithm SHA256 $sdl3Path).Hash.ToLowerInvariant()
if ($actualSdl3Hash -ne $expectedSdl3Hash) {
    throw "SDL3 runtime digest mismatch: $actualSdl3Hash"
}

$enhancements = @()
$enhanceVersion = $null
if ($Edition -eq 'full') {
    if (-not (Test-Path $enhanceManifestPath)) {
        throw "Full edition requires an enhancement pack manifest at $enhanceManifestPath. Run tools/Get-EnhancementPack.ps1 or remove -Edition full."
    }
    $enhanceManifest = Get-Content $enhanceManifestPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$enhanceManifest.version)) {
        throw 'Enhancement pack manifest must declare a version.'
    }
    $enhanceVersion = [string]$enhanceManifest.version
    $enhanceFiles = @($enhanceManifest.files)
    if ($enhanceFiles.Count -eq 0) { throw 'Enhancement pack manifest must list files.' }
    foreach ($entry in $enhanceFiles) {
        $relative = ([string]$entry.path).Trim().Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative.StartsWith('/') -or $relative.Contains('..') -or
            [System.IO.Path]::IsPathRooted($relative)) {
            throw "Enhancement pack file path is unsafe: $relative"
        }
        $fullPath = Join-Path $enhanceDirectory ($relative.Replace('/', '\'))
        if (-not (Test-Path $fullPath)) { throw "Enhancement pack file is missing: $relative" }
        $expectedHash = ([string]$entry.sha256).Trim().ToLowerInvariant()
        if ($expectedHash -notmatch '^[0-9a-f]{64}$') { throw "Enhancement pack hash is invalid for $relative" }
        $actualHash = (Get-FileHash $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $expectedHash) { throw "Enhancement pack digest mismatch for $relative" }
        $enhancements += [ordered]@{
            path = "runtime/enhance/$relative"
            sha256 = $actualHash
            size = [long](Get-Item $fullPath).Length
        }
    }
    Write-Output "Enhancement pack: $enhanceVersion ($($enhancements.Count) files)"
} else {
    Write-Output 'Edition lite: no enhancement pack will be bundled.'
}

Remove-Item $publishDirectory, $symbolsDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publishDirectory, $symbolsDirectory, $releaseDirectory | Out-Null
if (Test-Path $releaseDirectory) {
    Get-ChildItem $releaseDirectory -File | Where-Object {
        $_.Name -match "-$Edition-(portable|symbols|setup)" -or
        $_.Name -eq "release-manifest-$Edition.json" -or
        $_.Name -eq "windows-update-$Edition.json"
    } | Remove-Item -Force -ErrorAction SilentlyContinue
}

Invoke-Checked {
    dotnet publish $project -c Release -r $RuntimeIdentifier --self-contained true -p:Version=$Version -p:Edition=$Edition -p:PublishTrimmed=false -o $publishDirectory
} 'dotnet publish failed'

$publishedSdl3Path = Join-Path $publishDirectory 'runtime\sdl3\SDL3.dll'
if (-not (Test-Path $publishedSdl3Path)) {
    throw "SDL3 runtime was not copied into the publish output: $publishedSdl3Path"
}
$publishedSdl3Hash = (Get-FileHash -Algorithm SHA256 $publishedSdl3Path).Hash.ToLowerInvariant()
if ($publishedSdl3Hash -ne $actualSdl3Hash) {
    throw "Published SDL3 runtime digest mismatch: $publishedSdl3Hash"
}

foreach ($file in @('LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item (Join-Path $root $file) $publishDirectory
}
[IO.File]::WriteAllText((Join-Path $publishDirectory 'edition.txt'), $Edition, [Text.UTF8Encoding]::new($false))
if ($Edition -eq 'full') {
    $publishedEnhance = Join-Path $publishDirectory 'runtime\enhance'
    if (Test-Path $publishedEnhance) { Remove-Item $publishedEnhance -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $publishedEnhance | Out-Null
    Copy-Item (Join-Path $enhanceDirectory '*') $publishedEnhance -Recurse -Force
    foreach ($enhancement in $enhancements) {
        $relative = $enhancement.path.Substring('runtime/enhance/'.Length)
        $published = Join-Path $publishDirectory ("runtime\enhance\" + $relative.Replace('/', '\'))
        if (-not (Test-Path $published)) { throw "Enhancement file missing from publish output: $relative" }
        if ((Get-FileHash $published -Algorithm SHA256).Hash.ToLowerInvariant() -ne $enhancement.sha256) {
            throw "Published enhancement digest mismatch: $relative"
        }
    }
} elseif (Test-Path (Join-Path $publishDirectory 'runtime\enhance')) {
    throw 'Edition lite publish output must not contain runtime/enhance.'
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
    edition = $Edition
    enhancements = @($enhancements)
    enhancementPackVersion = $enhanceVersion
    libmpv = [ordered]@{
        version = '20261111-git-bcde6b93cc'
        library = 'runtime/libmpv/libmpv-2.dll'
        librarySha256 = $actualLibMpvHash
        distribution = 'shinchiro/mpv-winbuild-cmake'
    }
    sdl3 = [ordered]@{
        version = '3.4.20'
        library = 'runtime/sdl3/SDL3.dll'
        librarySha256 = $actualSdl3Hash
        distribution = 'libsdl-org/SDL'
    }
}
$manifestPath = Join-Path $releaseDirectory "release-manifest-$Edition.json"
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Copy-Item $manifestPath (Join-Path $publishDirectory 'release-manifest.json')

$symbolFiles = Get-ChildItem $publishDirectory -Filter *.pdb -File -Recurse
$symbolsArchive = Join-Path $releaseDirectory "MiruPlay-$Version-$RuntimeIdentifier-$Edition-symbols.zip"
if ($symbolFiles.Count -gt 0) {
    foreach ($symbol in $symbolFiles) { Copy-Item $symbol.FullName $symbolsDirectory }
    Compress-Archive -Path (Join-Path $symbolsDirectory '*') -DestinationPath $symbolsArchive -CompressionLevel Optimal
    $symbolFiles | Remove-Item -Force
}

$portableArchive = Join-Path $releaseDirectory "MiruPlay-$Version-$RuntimeIdentifier-$Edition-portable.zip"
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $portableArchive -CompressionLevel Optimal

if (-not $SkipInstaller) {
    $iscc = Find-Iscc
    $versionMatch = [regex]::Match($Version, '^(\d+)\.(\d+)\.(\d+)')
    $numericVersion = "$($versionMatch.Groups[1].Value).$($versionMatch.Groups[2].Value).$($versionMatch.Groups[3].Value).0"
    $setupBaseName = "MiruPlay-$Version-$RuntimeIdentifier-$Edition-setup"
    $editionIsFull = if ($Edition -eq 'full') { '1' } else { '0' }
    & $iscc /Qp "/DSourceDir=$publishDirectory" "/DAppVersion=$Version" "/DVersionNumeric=$numericVersion" "/DOutputDir=$releaseDirectory" "/DOutputBaseFilename=$setupBaseName" "/DMyEdition=$Edition" "/DMyEditionIsFull=$editionIsFull" $installerScript | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed (exit code $LASTEXITCODE)" }
    $setupPath = Join-Path $releaseDirectory "$setupBaseName.exe"
    if ($signed) { Sign-File $setupPath $signTool }

    $setupAsset = Get-Item $setupPath
    $tagName = "v$Version"
    $releaseRoot = "https://github.com/$GitHubRepository"
    $escapedTag = [Uri]::EscapeDataString($tagName)
    $escapedAsset = [Uri]::EscapeDataString($setupAsset.Name)
    $windowsUpdate = [ordered]@{
        versionName = "$Version.0"
        versionCode = $null
        releaseName = "MiruPlay $Version ($Edition)"
        tagName = $tagName
        publishedAt = [DateTimeOffset]::UtcNow.ToString('O')
        releaseUrl = "$releaseRoot/releases/tag/$escapedTag"
        edition = $Edition
        enhancements = @($enhancements)
        assetName = $setupAsset.Name
        assetSizeBytes = [long]$setupAsset.Length
        downloadUrl = "$releaseRoot/releases/download/$escapedTag/$escapedAsset"
        sha256 = (Get-FileHash $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $windowsUpdatePath = Join-Path $releaseDirectory "windows-update-$Edition.json"
    [IO.File]::WriteAllText($windowsUpdatePath, ($windowsUpdate | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    Write-Output "Windows update manifest: $windowsUpdatePath"
}

$checksumPath = Join-Path $releaseDirectory 'SHA256SUMS.txt'
Get-ChildItem $releaseDirectory -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object Name |
    ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
    Set-Content $checksumPath -Encoding ascii

Write-Output "Release artifacts: $releaseDirectory"
Get-ChildItem $releaseDirectory -File | Sort-Object Name | Select-Object Name, Length
