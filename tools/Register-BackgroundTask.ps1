param(
    [ValidateSet('Register', 'Unregister')]
    [string]$Action = 'Register',
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
$taskName = 'MiruPlay Windows Background'

if ($Action -eq 'Unregister') {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "Removed current-user task: $taskName"
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $PSScriptRoot '..\src\MiruPlay.Windows\bin\Release\net10.0-windows\MiruPlay.exe'
}
$ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw "MiruPlay executable not found: $ExecutablePath"
}

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$taskAction = New-ScheduledTaskAction -Execute $ExecutablePath -Argument '--background'
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
$principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $taskName -Action $taskAction -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Write-Output "Registered current-user logon task: $taskName"
