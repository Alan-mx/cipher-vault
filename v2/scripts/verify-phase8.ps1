[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ArtifactRoot,
    [Parameter(Mandatory)]
    [string] $EvidenceRoot,
    [string] $ProbeProject = ""
)

$ErrorActionPreference = "Stop"
$v2Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifact = (Resolve-Path -LiteralPath $ArtifactRoot).Path
$version = "2.0.0-rc.1"
$platform = "windows"
$policyMarker = "phase8-windows-acceptance-marker"
$password = "phase8 windows acceptance password"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runRoot = Join-Path $v2Root "artifacts/phase8-verification/windows/$stamp"
$archive = Join-Path $artifact "CipherVault-$version-win-x64.zip"
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
    throw "windows_archive_missing"
}
$probeProjectPath = if ([string]::IsNullOrWhiteSpace($ProbeProject)) {
    Join-Path $v2Root "tests/CipherVault.Phase8.ProcessProbe/CipherVault.Phase8.ProcessProbe.csproj"
}
else {
    (Resolve-Path -LiteralPath $ProbeProject).Path
}
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$probeDll = Join-Path $v2Root "tests/CipherVault.Phase8.ProcessProbe/bin/Release/net10.0/CipherVault.Phase8.ProcessProbe.dll"

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$extractRoot = Join-Path $runRoot "extracted"
Microsoft.PowerShell.Archive\Expand-Archive `
    -LiteralPath $archive `
    -DestinationPath $extractRoot
$packageRoot = Get-ChildItem -LiteralPath $extractRoot -Directory |
    Where-Object { $_.Name -eq "CipherVault-$version-win-x64" } |
    Select-Object -First 1
if ($null -eq $packageRoot) {
    throw "windows_package_root_missing"
}
$server = Join-Path $packageRoot.FullName "CipherVault.Server.exe"
if (-not (Test-Path -LiteralPath $server -PathType Leaf)) {
    throw "windows_server_missing"
}

Push-Location $v2Root
try {
    dotnet build $probeProjectPath -c Release --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) {
        throw "process_probe_build_failed"
    }
}
finally {
    Pop-Location
}

function Start-Probe {
    param(
        [string] $Mode,
        [string] $Data,
        [string] $Config,
        [string] $Backup,
        [string] $ReadyFile,
        [string] $ContinueFile,
        [string] $ProbeEvidence,
        [string] $Package = "",
        [string] $Target = ""
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment["PATH"] = "$env:SystemRoot\System32;$env:SystemRoot"
    [void] $startInfo.Environment.Remove("DOTNET_ROOT")
    [void] $startInfo.Environment.Remove("DOTNET_ROOT_X64")
    foreach ($argument in @(
        $probeDll,
        $Mode,
        "--server", $server,
        "--data", $Data,
        "--config", $Config,
        "--backup", $Backup,
        "--evidence", $ProbeEvidence,
        "--shutdown-timeout-seconds", "30"
    )) {
        $startInfo.ArgumentList.Add($argument)
    }
    if ($ReadyFile) {
        $startInfo.ArgumentList.Add("--ready-file")
        $startInfo.ArgumentList.Add($ReadyFile)
    }
    if ($ContinueFile) {
        $startInfo.ArgumentList.Add("--continue-file")
        $startInfo.ArgumentList.Add($ContinueFile)
    }
    if ($Package) {
        $startInfo.ArgumentList.Add("--package")
        $startInfo.ArgumentList.Add($Package)
    }
    if ($Target) {
        $startInfo.ArgumentList.Add("--target")
        $startInfo.ArgumentList.Add($Target)
    }
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "probe_start_failed"
    }
    return @{
        Process = $process
        StartInfo = $startInfo
    }
}

function Complete-Probe {
    param(
        [hashtable] $Probe,
        [string] $Name
    )

    $process = $Probe.Process
    $process.WaitForExit()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $logRoot = Join-Path $runRoot "logs"
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $logRoot "$Name.stdout.log"),
        $stdout)
    [IO.File]::WriteAllText(
        (Join-Path $logRoot "$Name.stderr.log"),
        $stderr)
    if ($process.ExitCode -ne 0) {
        throw "${Name}_failed"
    }
}

function Wait-Path {
    param(
        [string] $Path,
        [int] $Seconds = 45
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            return
        }
        Start-Sleep -Milliseconds 200
    }
    throw "probe_ready_timeout"
}

function Convert-McpPayload {
    param([string] $Payload)

    try {
        return $Payload | ConvertFrom-Json -Depth 20
    }
    catch {
        $data = $Payload -split "`r?`n" |
            Where-Object { $_.StartsWith("data:", [StringComparison]::Ordinal) } |
            Select-Object -First 1
        if (-not $data) {
            throw "mcp_response_invalid"
        }
        return $data.Substring(5).Trim() | ConvertFrom-Json -Depth 20
    }
}

function Invoke-Mcp {
    param(
        [string] $Token,
        [int] $Id,
        [string] $Method,
        [hashtable] $Params
    )

    $body = @{
        jsonrpc = "2.0"
        id = $Id
        method = $Method
        params = $Params
    } | ConvertTo-Json -Depth 20 -Compress
    $response = Invoke-WebRequest `
        -Uri "$baseUrl/mcp" `
        -Method Post `
        -Headers @{
            Authorization = "Bearer $Token"
            Accept = "application/json, text/event-stream"
        } `
        -ContentType "application/json" `
        -Body $body
    return Convert-McpPayload $response.Content
}

$baseUrl = "http://127.0.0.1:43127"
$data = Join-Path $runRoot "data"
$config = Join-Path $runRoot "config"
$backups = Join-Path $runRoot "backups"
New-Item -ItemType Directory -Path $data,$config,$backups -Force | Out-Null
$ready = Join-Path $runRoot "ready.json"
$continue = Join-Path $runRoot "continue"
$lifecycleEvidence = Join-Path $runRoot "process-resources.json"
$probe = Start-Probe `
    -Mode "run-server" `
    -Data $data `
    -Config $config `
    -Backup $backups `
    -ReadyFile $ready `
    -ContinueFile $continue `
    -ProbeEvidence $lifecycleEvidence
try {
    Wait-Path $ready
    $status = Invoke-RestMethod -Uri "$baseUrl/api/vault/status"
    if ($status.state -ne "uninitialized") {
        throw "windows_fresh_state_invalid"
    }

    $versionResponse = Invoke-WebRequest -Uri "$baseUrl/api/version"
    $versionInfo = $versionResponse.Content | ConvertFrom-Json
    if ($versionInfo.productVersion -ne $version -or
        $versionInfo.rid -ne "win-x64" -or
        $versionInfo.commit -notmatch '^[0-9a-f]{40}$') {
        throw "windows_version_invalid"
    }
    if ($versionResponse.Headers["Cache-Control"] -notcontains "no-store") {
        throw "windows_version_no_store_missing"
    }

    $index = Invoke-WebRequest -Uri "$baseUrl/"
    if ($index.Content.IndexOf(
            '<div id="app"></div>',
            [StringComparison]::Ordinal) -lt 0) {
        throw "windows_index_invalid"
    }
    if ($index.Headers["Content-Security-Policy"] -notcontains
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'") {
        throw "windows_csp_invalid"
    }
    if ($index.Headers["X-Frame-Options"] -notcontains "DENY" -or
        $index.Headers["X-Content-Type-Options"] -notcontains "nosniff" -or
        $index.Headers["Referrer-Policy"] -notcontains "no-referrer") {
        throw "windows_security_headers_invalid"
    }
    $assetPath = [regex]::Match(
        $index.Content,
        '/assets/[^"'']+\.js').Value
    if (-not $assetPath) {
        throw "windows_asset_path_missing"
    }
    $asset = Invoke-WebRequest -Uri "$baseUrl$assetPath"
    if ($asset.RawContentLength -le 0) {
        throw "windows_asset_invalid"
    }

    $setupBody = @{
        password = $password
        riskAccepted = $true
    } | ConvertTo-Json -Compress
    $setup = Invoke-WebRequest `
        -Uri "$baseUrl/api/vault/setup" `
        -Method Post `
        -ContentType "application/json" `
        -Body $setupBody `
        -SessionVariable webSession
    $unlockBody = @{ password = $password } | ConvertTo-Json -Compress
    $unlock = Invoke-WebRequest `
        -Uri "$baseUrl/api/vault/unlock" `
        -Method Post `
        -WebSession $webSession `
        -ContentType "application/json" `
        -Body $unlockBody
    $unlocked = $unlock.Content | ConvertFrom-Json
    if ($unlocked.state -ne "unlocked") {
        throw "windows_unlock_failed"
    }

    $antiforgery = Invoke-RestMethod `
        -Uri "$baseUrl/api/vault/antiforgery" `
        -WebSession $webSession
    $tokenResponse = Invoke-WebRequest `
        -Uri "$baseUrl/api/mcp/token/rotate" `
        -Method Post `
        -WebSession $webSession `
        -Headers @{ "X-CSRF-TOKEN" = $antiforgery.token }
    $mcpToken = ($tokenResponse.Content | ConvertFrom-Json).token
    if ([string]::IsNullOrWhiteSpace($mcpToken)) {
        throw "windows_mcp_token_missing"
    }

    $wrong = Invoke-WebRequest `
        -Uri "$baseUrl/mcp" `
        -SkipHttpErrorCheck `
        -Method Post `
        -Headers @{
            Authorization = "Bearer invalid"
            Accept = "application/json, text/event-stream"
        } `
        -ContentType "application/json" `
        -Body '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}'
    if ($wrong.StatusCode -ne 401) {
        throw "windows_mcp_wrong_token_not_rejected"
    }

    $initialize = Invoke-Mcp `
        -Token $mcpToken `
        -Id 1 `
        -Method "initialize" `
        -Params @{
            protocolVersion = "2025-06-18"
            capabilities = @{}
            clientInfo = @{ name = "phase8-windows"; version = "1.0" }
        }
    if ($null -eq $initialize.result.serverInfo) {
        throw "windows_mcp_initialize_failed"
    }
    $tools = Invoke-Mcp `
        -Token $mcpToken `
        -Id 2 `
        -Method "tools/list" `
        -Params @{}
    if (($tools.result.tools | Measure-Object).Count -lt 10) {
        throw "windows_mcp_tools_failed"
    }
    $vaultStatus = Invoke-Mcp `
        -Token $mcpToken `
        -Id 3 `
        -Method "tools/call" `
        -Params @{ name = "vault_status"; arguments = @{} }
    if ($null -eq $vaultStatus.result) {
        throw "windows_mcp_vault_status_failed"
    }

    $backupResponse = Invoke-WebRequest `
        -Uri "$baseUrl/api/backups" `
        -Method Post `
        -WebSession $webSession `
        -Headers @{ "X-CSRF-TOKEN" = $antiforgery.token } `
        -Body ""
    $backup = $backupResponse.Content | ConvertFrom-Json
    if (-not $backup.backup.fileName.EndsWith(
            ".cvbackup",
            [StringComparison]::Ordinal)) {
        throw "windows_backup_create_failed"
    }
    $backupFile = Join-Path $runRoot $backup.backup.fileName
    Invoke-WebRequest `
        -Uri "$baseUrl/api/backups/$([Uri]::EscapeDataString($backup.backup.fileName))/download" `
        -WebSession $webSession `
        -OutFile $backupFile
    if ((Get-Item -LiteralPath $backupFile).Length -le 0) {
        throw "windows_backup_download_failed"
    }

    Set-Content -LiteralPath $continue -Value $policyMarker -NoNewline
    Complete-Probe -Probe $probe -Name "first-lifecycle"
}
catch {
    if (-not $probe.Process.HasExited) {
        $probe.Process.Kill($true)
    }
    throw
}

$restoreTarget = Join-Path $runRoot "restored"
$pipeName = "ciphervault-phase8-$([Guid]::NewGuid().ToString('N'))"
$pipe = [IO.Pipes.NamedPipeServerStream]::new(
    $pipeName,
    [IO.Pipes.PipeDirection]::Out,
    1,
    [IO.Pipes.PipeTransmissionMode]::Byte,
    [IO.Pipes.PipeOptions]::Asynchronous)
$pipeWait = $pipe.WaitForConnectionAsync()
$restoreStart = [Diagnostics.ProcessStartInfo]::new($dotnet)
$restoreStart.UseShellExecute = $false
$restoreStart.Environment["PATH"] = "$env:SystemRoot\System32;$env:SystemRoot"
[void] $restoreStart.Environment.Remove("DOTNET_ROOT")
[void] $restoreStart.Environment.Remove("DOTNET_ROOT_X64")
foreach ($argument in @(
    $probeDll,
    "restore",
    "--server", $server,
    "--data", $data,
    "--config", $config,
    "--backup", $backups,
    "--package", $backupFile,
    "--target", $restoreTarget,
    "--evidence", (Join-Path $runRoot "restore-probe.json"),
    "--password-pipe", $pipeName
)) {
    $restoreStart.ArgumentList.Add($argument)
}
$restoreProcess = [Diagnostics.Process]::Start($restoreStart)
if (-not $pipeWait.Wait(10000)) {
    throw "windows_restore_pipe_timeout"
}
$pipeWriter = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false))
$pipeWriter.WriteLine($password)
$pipeWriter.Flush()
$pipeWriter.Dispose()
$pipe.Dispose()
$restoreProcess.WaitForExit()
if ($restoreProcess.ExitCode -ne 0 -or
    -not (Test-Path -LiteralPath $restoreTarget -PathType Container)) {
    throw "windows_offline_restore_failed"
}

$restoredReady = Join-Path $runRoot "restored-ready.json"
$restoredContinue = Join-Path $runRoot "restored-continue"
$restoredData = $restoreTarget
$restoredConfig = Join-Path $runRoot "restored-config"
$restoredBackups = Join-Path $runRoot "restored-backups"
New-Item -ItemType Directory `
    -Path $restoredConfig,$restoredBackups `
    -Force | Out-Null
$restoredProbe = Start-Probe `
    -Mode "run-server" `
    -Data $restoredData `
    -Config $restoredConfig `
    -Backup $restoredBackups `
    -ReadyFile $restoredReady `
    -ContinueFile $restoredContinue `
    -ProbeEvidence (Join-Path $runRoot "restored-process-resources.json")
try {
    Wait-Path $restoredReady
    $readyState = Get-Content -LiteralPath $restoredReady -Raw |
        ConvertFrom-Json
    if ($readyState.state -ne "locked") {
        throw "windows_restored_not_locked"
    }
    $unlockBody = @{ password = $password } | ConvertTo-Json -Compress
    $restoredUnlock = Invoke-WebRequest `
        -Uri "$baseUrl/api/vault/unlock" `
        -Method Post `
        -ContentType "application/json" `
        -Body $unlockBody `
        -SessionVariable restoredSession
    if (($restoredUnlock.Content | ConvertFrom-Json).state -ne "unlocked") {
        throw "windows_restored_unlock_failed"
    }
    Set-Content `
        -LiteralPath $restoredContinue `
        -Value $policyMarker `
        -NoNewline
    Complete-Probe -Probe $restoredProbe -Name "restored-lifecycle"
}
catch {
    if (-not $restoredProbe.Process.HasExited) {
        $restoredProbe.Process.Kill($true)
    }
    throw
}
$evidenceDirectory = [IO.Path]::GetFullPath($EvidenceRoot)
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
$evidence = [ordered]@{
    platform = $platform
    environment = "local Windows host; child PATH stripped"
    artifactFileName = [IO.Path]::GetFileName($archive)
    artifactSha256 = $archiveHash.ToLowerInvariant()
    noPreinstalledDotnet = $false
    selfContainedChildPath = $true
    serverStarted = $true
    webStaticAssets = $true
    securityHeaders = $true
    versionVerified = $true
    mcpInitialize = $true
    mcpToolsList = $true
    mcpVaultStatus = $true
    wrongTokenRejected = $true
    backupCreated = $true
    offlineRestore = $true
    restoredVaultLocked = $true
    restoredVaultUnlock = $true
    gracefulShutdown = $true
    processResources = (
        Get-Content -LiteralPath $lifecycleEvidence -Raw |
            ConvertFrom-Json
    )
    checks = @(
        @{ name = "windows-artifact"; passed = $true },
        @{ name = "windows-runtime"; passed = $true },
        @{ name = "windows-web-mcp-backup"; passed = $true }
    )
}
[IO.File]::WriteAllText(
    (Join-Path $evidenceDirectory "windows.json"),
    ($evidence | ConvertTo-Json -Depth 10),
    [Text.UTF8Encoding]::new($false))
Write-Output "Phase 8 Windows artifact verification passed."
