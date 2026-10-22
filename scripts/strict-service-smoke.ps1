param(
    [string]$CliExe = "$env:ProgramFiles\CipherVault\CipherVault.Cli.exe",
    [string]$HostExe = "$env:ProgramFiles\CipherVault\CipherVault.Host.exe",
    [string]$UiExe = "$env:ProgramFiles\CipherVault\CipherVault.Ui.Wpf.exe",
    [string]$BrokerExe = "$env:ProgramFiles\CipherVault\CipherVault.Auth.Broker.exe",
    [string]$McpExe = "$env:ProgramFiles\CipherVault\CipherVault.Mcp.exe",
    [string]$SignerThumbprint,
    [switch]$RemoveData
)

$ErrorActionPreference = "Stop"

function Write-StepJson {
    param(
        [string]$Step,
        [int]$ExitCode,
        [bool]$Ok,
        $Output,
        [string]$Error
    )
    [ordered]@{
        step = $Step
        exitCode = $ExitCode
        ok = $Ok
        output = $Output
        error = if ([string]::IsNullOrWhiteSpace($Error)) { $null } else { $Error }
    } | ConvertTo-Json -Depth 20 -Compress
}

function Invoke-ServiceStep {
    param(
        [string]$Step,
        [string[]]$Arguments
    )
    $raw = & $CliExe @Arguments 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    $text = $raw.Trim()
    $parsed = $null
    if (-not [string]::IsNullOrWhiteSpace($text)) {
        try {
            $parsed = $text | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            $parsed = $text
        }
    }
    Write-StepJson `
        -Step $Step `
        -ExitCode $exitCode `
        -Ok ($exitCode -eq 0) `
        -Output $parsed `
        -Error ""
    if ($exitCode -ne 0) {
        throw "$Step failed with exit code $exitCode"
    }
}

$principal = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-StepJson `
        -Step "precheck" `
        -ExitCode 2 `
        -Ok $false `
        -Output $null `
        -Error "Administrator privileges are required."
    exit 2
}

foreach ($path in @($CliExe, $HostExe, $UiExe, $BrokerExe, $McpExe)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Write-StepJson `
            -Step "precheck" `
            -ExitCode 2 `
            -Ok $false `
            -Output $path `
            -Error "Required signed image was not found."
        exit 2
    }
}

$installArgs = @(
    "service", "install",
    "--host-exe", $HostExe,
    "--ui-exe", $UiExe,
    "--cli-exe", $CliExe,
    "--broker-exe", $BrokerExe,
    "--mcp-exe", $McpExe,
    "--no-start"
)
if (-not [string]::IsNullOrWhiteSpace($SignerThumbprint)) {
    $installArgs += @("--signer-thumbprint", $SignerThumbprint)
}
$removeArgs = @("service", "uninstall")
if ($RemoveData) {
    $removeArgs += "--remove-data"
}

$installed = $false
try {
    Invoke-ServiceStep -Step "install" -Arguments $installArgs
    $installed = $true
    Invoke-ServiceStep -Step "start" -Arguments @("service", "start")
    Invoke-ServiceStep -Step "status" -Arguments @("service", "status")
    Invoke-ServiceStep -Step "stop" -Arguments @("service", "stop")
    Invoke-ServiceStep -Step "uninstall" -Arguments $removeArgs
    $installed = $false
}
catch {
    Write-StepJson `
        -Step "smoke" `
        -ExitCode 1 `
        -Ok $false `
        -Output $null `
        -Error $_.Exception.Message
    if ($installed) {
        try {
            Invoke-ServiceStep -Step "cleanup-uninstall" -Arguments $removeArgs
        }
        catch {
            Write-StepJson `
                -Step "cleanup-uninstall" `
                -ExitCode 1 `
                -Ok $false `
                -Output $null `
                -Error $_.Exception.Message
        }
    }
    exit 1
}
