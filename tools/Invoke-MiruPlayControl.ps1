param(
    [Parameter(Mandatory)]
    [string]$Name
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$process = Get-Process -Name MiruPlay -ErrorAction Stop | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
$nameCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty,
    $Name)
$typeCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)
$condition = New-Object System.Windows.Automation.AndCondition($nameCondition, $typeCondition)
$element = $root.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $condition)
if ($null -eq $element) { throw "Control not found: $Name" }
$pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$pattern.Invoke()
Write-Output "invoked=$Name"
