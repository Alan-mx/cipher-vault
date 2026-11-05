[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ArtifactRoot,
    [Parameter(Mandatory)]
    [string] $EvidenceRoot,
    [Parameter(Mandatory)]
    [string] $ReleaseVersion
)

$ErrorActionPreference = "Stop"
$artifact = [IO.Path]::GetFullPath($ArtifactRoot)
$archive = Join-Path $artifact "CipherVault-$ReleaseVersion-win-x64.zip"
$wrapper = "CipherVault-$ReleaseVersion-win-x64"
$runRoot = Join-Path ([IO.Path]::GetTempPath()) `
    "cipher-vault-windows-verify-$([Guid]::NewGuid().ToString('N'))"
$process = $null

if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
    throw "windows_archive_missing"
}

try {
    $extractRoot = Join-Path $runRoot "extracted"
    Microsoft.PowerShell.Management\New-Item -ItemType Directory `
        -Path $extractRoot -Force | Out-Null
    Microsoft.PowerShell.Archive\Expand-Archive `
        -LiteralPath $archive `
        -DestinationPath $extractRoot
    $packageRoot = Get-ChildItem -LiteralPath $extractRoot -Directory |
        Where-Object { $_.Name -eq $wrapper } |
        Select-Object -First 1
    if ($null -eq $packageRoot) {
        throw "windows_package_root_missing"
    }
    $server = Join-Path $packageRoot.FullName "CipherVault.Server.exe"
    if (-not (Test-Path -LiteralPath $server -PathType Leaf)) {
        throw "windows_server_missing"
    }

    $stdout = Join-Path $runRoot "server.stdout.log"
    $stderr = Join-Path $runRoot "server.stderr.log"
    $startInfo = [Diagnostics.ProcessStartInfo]::new($server)
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment["CIPHERVAULT_DATA_DIR"] =
        Join-Path $runRoot "data"
    $startInfo.Environment["CIPHERVAULT_CONFIG_DIR"] =
        Join-Path $runRoot "config"
    $startInfo.Environment["CIPHERVAULT_BACKUP_DIR"] =
        Join-Path $runRoot "backups"
    $startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production"
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "windows_server_start_failed"
    }

    $baseUrl = "http://127.0.0.1:43127"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    $status = $null
    do {
        Start-Sleep -Milliseconds 250
        if ($process.HasExited) {
            $process.WaitForExit()
            throw (
                "windows_server_exited exitCode=$($process.ExitCode)\n" +
                $process.StandardOutput.ReadToEnd() + "\n" +
                $process.StandardError.ReadToEnd())
        }
        try {
            $status = Invoke-RestMethod -Uri "$baseUrl/api/vault/status" `
                -TimeoutSec 2
        }
        catch {
            $status = $null
        }
    }
    while ($status -is [string] -or
        $null -eq $status -and
        [DateTimeOffset]::UtcNow -lt $deadline)
    if ($null -eq $status -or $status.state -ne "uninitialized") {
        throw "windows_status_invalid"
    }

    $version = Invoke-RestMethod -Uri "$baseUrl/api/version" -TimeoutSec 10
    if ($version.productVersion -ne $ReleaseVersion -or
        $version.rid -ne "win-x64" -or
        $version.commit -notmatch '^[0-9a-f]{40}$') {
        throw "windows_version_invalid"
    }
    $index = Invoke-WebRequest -Uri "$baseUrl/" -TimeoutSec 10
    if ($index.Content.IndexOf(
            '<div id="app"></div>',
            [StringComparison]::Ordinal) -lt 0) {
        throw "windows_index_invalid"
    }
    $assetPath = [regex]::Match(
        $index.Content,
        '/assets/[^"'']+\.js').Value
    if (-not $assetPath) {
        throw "windows_asset_missing"
    }
    $asset = Invoke-WebRequest -Uri "$baseUrl$assetPath" -TimeoutSec 10
    if ($asset.RawContentLength -le 0) {
        throw "windows_asset_invalid"
    }

    $evidenceDirectory = [IO.Path]::GetFullPath($EvidenceRoot)
    Microsoft.PowerShell.Management\New-Item -ItemType Directory `
        -Path $evidenceDirectory -Force | Out-Null
    $evidence = [ordered]@{
        platform = "windows"
        artifactFileName = [IO.Path]::GetFileName($archive)
        artifactSha256 = (
            Get-FileHash -LiteralPath $archive -Algorithm SHA256
        ).Hash.ToLowerInvariant()
        rid = "win-x64"
        serverStarted = $true
        freshStateValidated = $true
        versionVerified = $true
        webStaticAssets = $true
        checks = @(
            @{ name = "windows-artifact"; passed = $true }
            @{ name = "windows-runtime"; passed = $true }
            @{ name = "windows-web-assets"; passed = $true }
        )
        passed = $true
    }
    [IO.File]::WriteAllText(
        (Join-Path $evidenceDirectory "windows.json"),
        ($evidence | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
}
finally {
    if ($process -is [Diagnostics.Process] -and -not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit(5000)
    }
    if (Test-Path -LiteralPath $runRoot) {
        Microsoft.PowerShell.Management\Remove-Item `
            -LiteralPath $runRoot `
            -Recurse `
            -Force
    }
}
