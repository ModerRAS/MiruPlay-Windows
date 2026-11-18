param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\runtime\shaders\anime4k')
)

$ErrorActionPreference = 'Stop'
$version = '4.0.1'
$asset = 'Anime4K_v4.0.zip'
$expectedArchiveHash = '139cd282086457c5adc79caf7b75b8b825091d71c9b54958c18745fea62d7ed7'
$url = "https://github.com/bloc97/Anime4K/releases/download/v$version/$asset"
$destination = [System.IO.Path]::GetFullPath($Destination)
$archiveDirectory = Join-Path $destination '.extract-anime4k'
$archivePath = Join-Path $archiveDirectory $asset

# Official Anime4K v4.0.1 files used by the fast (low-end GPU) preset chains.
$files = [ordered]@{
    'Anime4K_Clamp_Highlights.glsl'          = '8c5fb67c76bed3021f8a27b050c3b97a6ac1b284f9ce91c04189015c354c0217'
    'Anime4K_Restore_CNN_M.glsl'             = '67ea3ed26539e8de3b7d307688535d2ff17e8d147e11dda0247da7770dbecf41'
    'Anime4K_Restore_CNN_S.glsl'             = '97c24dc370ab300c108bfaa09db7f175aeff343674842c299cf3940a3d330427'
    'Anime4K_Restore_CNN_Soft_M.glsl'        = 'a78a2c76898e08e09e442a9628c64208c26e8e15789649b8755223f009794c02'
    'Anime4K_Upscale_CNN_x2_M.glsl'          = '716e02098a68f0d648761f2b96b4dd139e1cb09b174bb369fca3aa34328fff7e'
    'Anime4K_Upscale_CNN_x2_S.glsl'          = '4c53ec2e287908f7ee7bcb266b0170421626d663576468b7d7dafc62962649a4'
    'Anime4K_Upscale_Denoise_CNN_x2_M.glsl'  = '8c72b042e2301fe66a45c3089720459148e2504cd72af16f9c0d5017ff14181e'
    'Anime4K_AutoDownscalePre_x2.glsl'       = '9141668ced0b26512253e6396e805820716f35b57c92950d9da489f8b96a7ba4'
    'Anime4K_AutoDownscalePre_x4.glsl'       = 'dadb7b713cfa1d810c55b5deff616072f3390e546fed1e6a54f80ea555f7b95d'
}

New-Item -ItemType Directory -Force -Path $destination | Out-Null
Remove-Item $archiveDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $archiveDirectory | Out-Null
Invoke-WebRequest -Uri $url -OutFile $archivePath
$actualArchiveHash = (Get-FileHash -Algorithm SHA256 $archivePath).Hash.ToLowerInvariant()
if ($actualArchiveHash -ne $expectedArchiveHash) {
    Remove-Item $archiveDirectory -Recurse -Force -ErrorAction SilentlyContinue
    throw "Anime4K archive digest mismatch: $actualArchiveHash"
}

Expand-Archive -Path $archivePath -DestinationPath $archiveDirectory -Force
foreach ($file in $files.Keys) {
    $extracted = Join-Path $archiveDirectory $file
    if (-not (Test-Path $extracted)) {
        Remove-Item $archiveDirectory -Recurse -Force -ErrorAction SilentlyContinue
        throw "Anime4K archive is missing $file."
    }
    $actualHash = (Get-FileHash -Algorithm SHA256 $extracted).Hash.ToLowerInvariant()
    if ($actualHash -ne $files[$file]) {
        Remove-Item $archiveDirectory -Recurse -Force -ErrorAction SilentlyContinue
        throw "Anime4K shader digest mismatch for ${file}: $actualHash"
    }
    Copy-Item $extracted (Join-Path $destination $file) -Force
}

Remove-Item $archiveDirectory -Recurse -Force -ErrorAction SilentlyContinue
Write-Output "archiveSha256=$actualArchiveHash"
Write-Output "files=$($files.Count)"
foreach ($file in $files.Keys) { Write-Output ("file.{0}={1}" -f $file, $files[$file]) }
