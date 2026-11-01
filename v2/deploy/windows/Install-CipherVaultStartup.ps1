[CmdletBinding()]
param(
    [switch] $Uninstall
)

$ErrorActionPreference = "Stop"
$taskName = "CipherVault.Server"
$taskPath = "\"

if ($Uninstall) {
    $existing = Get-ScheduledTask `
        -TaskName $taskName `
        -TaskPath $taskPath `
        -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        Unregister-ScheduledTask `
            -TaskName $taskName `
            -TaskPath $taskPath `
            -Confirm:$false `
            -ErrorAction Stop
    }
    Write-Host "CipherVault startup task removed."
    exit 0
}

$startScript = Join-Path $PSScriptRoot "Start-CipherVault.ps1"
$server = Join-Path $PSScriptRoot "CipherVault.Server.exe"
if (-not (Test-Path -LiteralPath $startScript -PathType Leaf) `
    -or -not (Test-Path -LiteralPath $server -PathType Leaf)) {
    throw "The CipherVault startup files are incomplete."
}

$currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$powershell = Join-Path $env:SystemRoot `
    "System32\WindowsPowerShell\v1.0\powershell.exe"
$arguments = "-NoProfile -NonInteractive -WindowStyle Hidden " +
    "-ExecutionPolicy Bypass -File `"$startScript`""
$action = New-ScheduledTaskAction `
    -Execute $powershell `
    -Argument $arguments `
    -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal `
    -UserId $currentSid `
    -LogonType Interactive `
    -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask `
    -TaskName $taskName `
    -TaskPath $taskPath `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Force `
    -ErrorAction Stop | Out-Null

Write-Host "CipherVault startup task registered for the current user."
Write-Host "Run this script with -Uninstall to remove it."
