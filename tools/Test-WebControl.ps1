param(
    [int]$Port = 9978
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$tokenPath = Join-Path $env:LOCALAPPDATA 'MiruPlay\web-control-token.bin'
$entropy = [System.Text.Encoding]::UTF8.GetBytes('MiruPlay.Windows.WebControl.v1')
$encrypted = [System.IO.File]::ReadAllBytes($tokenPath)
$token = [System.Text.Encoding]::UTF8.GetString(
    [System.Security.Cryptography.ProtectedData]::Unprotect(
        $encrypted,
        $entropy,
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser))
$baseUrl = "http://127.0.0.1:$Port"

$unauthorizedStatus = 0
try {
    Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/api/info" | Out-Null
}
catch {
    $unauthorizedStatus = [int]$_.Exception.Response.StatusCode
}

$headers = @{ 'X-MiruPlay-Token' = $token }
$info = Invoke-RestMethod -Uri "$baseUrl/api/info" -Headers $headers
$library = Invoke-RestMethod -Uri "$baseUrl/api/library" -Headers $headers
$sources = Invoke-RestMethod -Uri "$baseUrl/api/sources" -Headers $headers
$localDirectories = Invoke-RestMethod -Uri "$baseUrl/api/local-directories" -Headers $headers
$smbSourceStatus = 0
try {
    Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$baseUrl/api/sources" -Headers $headers -ContentType 'application/json' -Body '{"name":"Unsupported smoke","type":"SMB","location":"smb://example.invalid/share","contentMode":"ANIME","recognitionMode":"MLIP"}' | Out-Null
}
catch {
    $smbSourceStatus = [int]$_.Exception.Response.StatusCode
}
$sourcesAfterUnsupported = Invoke-RestMethod -Uri "$baseUrl/api/sources" -Headers $headers

$idleCommandStatus = 0
try {
    Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$baseUrl/api/playback/command" -Headers $headers -ContentType 'application/json' -Body '{"command":"pause"}' | Out-Null
}
catch {
    $idleCommandStatus = [int]$_.Exception.Response.StatusCode
}

[pscustomobject]@{
    UnauthorizedStatus = $unauthorizedStatus
    InfoOk = $info.ok
    AppName = $info.data.appName
    Port = $info.data.port
    LibraryOk = $library.ok
    SeriesCount = @($library.data.allAnime).Count
    ContinueWatchingCount = @($library.data.continueWatching).Count
    SourceCount = @($sources.data).Count
    FirstSourceType = @($sources.data)[0].type
    LocalDriveCount = @($localDirectories.data.entries).Count
    UnsupportedSmbSourceStatus = $smbSourceStatus
    SourceCountAfterUnsupported = @($sourcesAfterUnsupported.data).Count
    IdlePlaybackCommandStatus = $idleCommandStatus
} | ConvertTo-Json
