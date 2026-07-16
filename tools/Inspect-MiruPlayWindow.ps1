param(
    [string]$OutputPath = "$env:TEMP\miruplay-windows-smoke.png"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MiruPlayNativeWindow {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
"@

$process = Get-Process -Name MiruPlay -ErrorAction Stop | Select-Object -First 1
$deadline = [DateTime]::UtcNow.AddSeconds(15)
while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds 250
    $process.Refresh()
}
if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
    throw 'MiruPlay did not create a main window within 15 seconds.'
}

[MiruPlayNativeWindow]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 500
$root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
$bounds = $root.Current.BoundingRectangle
$left = [int][Math]::Floor($bounds.Left)
$top = [int][Math]::Floor($bounds.Top)
$width = [int][Math]::Ceiling($bounds.Width)
$height = [int][Math]::Ceiling($bounds.Height)
$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.CopyFromScreen($left, $top, 0, 0, $bitmap.Size)
    $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

$elements = $root.FindAll(
    [System.Windows.Automation.TreeScope]::Subtree,
    [System.Windows.Automation.Condition]::TrueCondition)
$names = foreach ($element in $elements) {
    $name = $element.Current.Name
    if (-not [string]::IsNullOrWhiteSpace($name) -and $name.Length -le 120) { $name }
}

[pscustomobject]@{
    ProcessId = $process.Id
    Title = $process.MainWindowTitle
    Width = $width
    Height = $height
    Responding = $process.Responding
    ElementCount = $elements.Count
    VisibleNames = @($names | Select-Object -Unique -First 80)
    Screenshot = $OutputPath
} | ConvertTo-Json -Depth 4
