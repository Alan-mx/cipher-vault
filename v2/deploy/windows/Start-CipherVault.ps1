[CmdletBinding()]
param(
    [switch] $OpenBrowser
)

$ErrorActionPreference = "Stop"
$server = Join-Path $PSScriptRoot "CipherVault.Server.exe"
$url = "http://127.0.0.1:43127"

if (-not (Test-Path -LiteralPath $server -PathType Leaf)) {
    throw "CipherVault.Server.exe was not found next to this script."
}

Write-Host "CipherVault: $url"
Write-Host "Press Ctrl+C to stop."

$browserJob = $null
if ($OpenBrowser) {
    $browserJob = Start-Job -ScriptBlock {
        param($targetUrl)
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            try {
                $response = Invoke-WebRequest `
                    -Uri "$targetUrl/api/vault/status" `
                    -UseBasicParsing `
                    -TimeoutSec 1
                if ($response.StatusCode -eq 200) {
                    Start-Process $targetUrl
                    return
                }
            }
            catch {
                Start-Sleep -Seconds 1
            }
        }
    } -ArgumentList $url
}

Push-Location -LiteralPath $PSScriptRoot
try {
    & $server
    $exitCode = $LASTEXITCODE
    if ($null -eq $exitCode) {
        $exitCode = 0
    }
    exit $exitCode
}
finally {
    Pop-Location
    if ($null -ne $browserJob) {
        Stop-Job -Job $browserJob -ErrorAction SilentlyContinue
        Remove-Job -Job $browserJob -Force -ErrorAction SilentlyContinue
    }
}
