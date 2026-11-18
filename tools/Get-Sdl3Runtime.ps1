param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\runtime\sdl3')
)

$ErrorActionPreference = 'Stop'
$version = '3.4.20'
$asset = 'SDL3-3.4.20-win32-x64.zip'
$expectedArchiveHash = '368bed8b24e44ad9ea372990814f2343fcfb9e468abb7f8f4a3bc146c7304752'
$expectedLibraryHash = '66844f10251cc1c10619ed5d8e4e3224f564900ee6d69ba87d63de782addae40'
$url = "https://github.com/libsdl-org/SDL/releases/download/release-$version/$asset"
$destination = [System.IO.Path]::GetFullPath($Destination)
$archive = Join-Path $destination $asset
$extractDirectory = Join-Path $destination '.extract-sdl3'
$libraryPath = Join-Path $destination 'SDL3.dll'

New-Item -ItemType Directory -Force -Path $destination | Out-Null
Remove-Item $extractDirectory -Recurse -Force -ErrorAction SilentlyContinue
Invoke-WebRequest -Uri $url -OutFile $archive
$actualArchiveHash = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
if ($actualArchiveHash -ne $expectedArchiveHash) {
    Remove-Item $archive -Force -ErrorAction SilentlyContinue
    throw "SDL3 archive digest mismatch: $actualArchiveHash"
}

Expand-Archive -Path $archive -DestinationPath $extractDirectory -Force
$extractedLibrary = Get-ChildItem $extractDirectory -Recurse -File -Filter 'SDL3.dll' | Select-Object -First 1
if (-not $extractedLibrary) {
    Remove-Item $archive, $extractDirectory -Recurse -Force -ErrorAction SilentlyContinue
    throw 'The verified archive did not contain SDL3.dll.'
}
$actualLibraryHash = (Get-FileHash -Algorithm SHA256 $extractedLibrary.FullName).Hash.ToLowerInvariant()
if ($actualLibraryHash -ne $expectedLibraryHash) {
    Remove-Item $archive, $extractDirectory -Recurse -Force -ErrorAction SilentlyContinue
    throw "SDL3 DLL digest mismatch: $actualLibraryHash"
}

Move-Item $extractedLibrary.FullName $libraryPath -Force
Remove-Item $archive, $extractDirectory -Recurse -Force -ErrorAction SilentlyContinue
Write-Output "archiveSha256=$actualArchiveHash"
Write-Output "sdl3Sha256=$actualLibraryHash"
Write-Output "sdl3Version=$version"
