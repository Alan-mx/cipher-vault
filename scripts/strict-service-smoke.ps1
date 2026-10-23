param(
    [string]$CliExe = "$env:ProgramFiles\CipherVault\CipherVault.Cli.exe",
    [string]$HostExe = "$env:ProgramFiles\CipherVault\CipherVault.Host.exe",
    [string]$UiExe = "$env:ProgramFiles\CipherVault\CipherVault.Ui.Wpf.exe",
    [string]$BrokerExe = "$env:ProgramFiles\CipherVault\CipherVault.Auth.Broker.exe",
    [string]$McpExe = "$env:ProgramFiles\CipherVault\CipherVault.Mcp.exe",
    [ValidatePattern("^[0-9A-Fa-f]{64}$")]
    [string]$SignerThumbprint,
    [switch]$RemoveData
)

$ErrorActionPreference = "Stop"
$serviceName = "CipherVaultHost"
$dataRoot = Join-Path $env:ProgramData "CipherVault"
$manifestPath = Join-Path $dataRoot "install-public.json"
$startedAt = Get-Date

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
    } | ConvertTo-Json -Depth 30 -Compress
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
    return $parsed
}

function Get-AccountSid {
    param([string]$Account)
    return ([Security.Principal.NTAccount]::new($Account)).Translate(
        [Security.Principal.SecurityIdentifier]).Value
}

function Get-SignerSha256 {
    param($Signature)
    return $Signature.SignerCertificate.GetCertHashString(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
}

function Get-ProcessEvidence {
    param(
        [int]$ProcessId,
        [string]$ExpectedUserSid,
        [string]$ExpectedImagePath,
        [string]$ExpectedSha256,
        [string]$ExpectedSigner
    )
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId"
    if ($null -eq $process) {
        return [ordered]@{
            processId = $ProcessId
            ok = $false
            error = "Process was not found."
        }
    }
    $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwner
    $ownerAccount = if ([string]::IsNullOrWhiteSpace($owner.Domain)) {
        [string]$owner.User
    }
    else {
        "$($owner.Domain)\$($owner.User)"
    }
    $ownerSid = Get-AccountSid $ownerAccount
    $imagePath = [string]$process.ExecutablePath
    $imagePathAvailable = -not [string]::IsNullOrWhiteSpace($imagePath)
    $actualSha256 = if ($imagePathAvailable -and
        (Test-Path -LiteralPath $imagePath)) {
        (Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash
    }
    else {
        $null
    }
    $signature = if ($imagePathAvailable -and
        (Test-Path -LiteralPath $imagePath)) {
        Get-AuthenticodeSignature -LiteralPath $imagePath
    }
    else {
        $null
    }
    $ok =
        [string]::Equals($ownerSid, $ExpectedUserSid, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($imagePath, $ExpectedImagePath, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($actualSha256, $ExpectedSha256, [StringComparison]::OrdinalIgnoreCase) -and
        $null -ne $signature -and
        $signature.Status -eq "Valid" -and
        [string]::Equals(
            (Get-SignerSha256 $signature),
            $ExpectedSigner,
            [StringComparison]::OrdinalIgnoreCase)
    return [ordered]@{
        processId = $ProcessId
        owner = $ownerAccount
        ownerSid = $ownerSid
        sessionId = [int]$process.SessionId
        imagePath = $imagePath
        sha256 = $actualSha256
        signature = if ($null -eq $signature) { "Missing" } else { [string]$signature.Status }
        signerThumbprint = if ($null -eq $signature) {
            $null
        }
        else {
            Get-SignerSha256 $signature
        }
        ok = $ok
    }
}

function Get-ManifestImage {
    param(
        [object]$Manifest,
        [string]$Kind
    )
    $images = @($Manifest.allowedServerImages) + @($Manifest.allowedClientImages)
    return $images |
        Where-Object {
            [string]::Equals(
                [string]$_.kind,
                $Kind,
                [StringComparison]::OrdinalIgnoreCase)
        } |
        Select-Object -First 1
}

function Read-TrimmedFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return ""
    }
    $value = Get-Content -Raw -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($null -eq $value) {
        return ""
    }
    return ([string]$value).Trim()
}

function Stop-InstalledClientProcesses {
    param([string[]]$ImagePaths)
    $normalizedPaths = @($ImagePaths | ForEach-Object {
        [IO.Path]::GetFullPath($_)
    })
    Get-CimInstance Win32_Process |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
            $normalizedPaths -contains $_.ExecutablePath
        } |
        ForEach-Object {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
    Start-Sleep -Milliseconds 300
}

function Wait-ServiceState {
    param(
        [string]$ExpectedState,
        [int]$TimeoutSeconds = 30
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        $actual = if ($null -eq $service) {
            "notInstalled"
        }
        else {
            [string]$service.Status
        }
        if ([string]::Equals(
                $actual,
                $ExpectedState,
                [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Start-ProcessAndInspect {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$ExpectedUserSid,
        [string]$ExpectedImagePath,
        [string]$ExpectedSha256,
        [string]$ExpectedSigner,
        [int]$InspectWithinMilliseconds = 5000,
        [switch]$CloseWindow
    )
    $stdout = Join-Path $env:TEMP "cipher-vault-smoke-$([guid]::NewGuid().ToString('N')).out"
    $stderr = Join-Path $env:TEMP "cipher-vault-smoke-$([guid]::NewGuid().ToString('N')).err"
    $startInfo = @{
        FilePath = $FilePath
        PassThru = $true
        RedirectStandardOutput = $stdout
        RedirectStandardError = $stderr
    }
    if ($CloseWindow) {
        $startInfo.WindowStyle = "Minimized"
    }
    else {
        $startInfo.NoNewWindow = $true
    }
    if ($Arguments.Count -gt 0) {
        $startInfo.ArgumentList = $Arguments
    }
    $process = Start-Process @startInfo
    $deadline = [DateTime]::UtcNow.AddMilliseconds($InspectWithinMilliseconds)
    $processEvidence = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $processEvidence = Get-CimInstance Win32_Process `
            -Filter "ProcessId=$($process.Id)"
        if ($null -ne $processEvidence -and
            -not [string]::IsNullOrWhiteSpace($processEvidence.ExecutablePath)) {
            break
        }
        Start-Sleep -Milliseconds 50
    }
    $evidence = Get-ProcessEvidence `
        -ProcessId $process.Id `
        -ExpectedUserSid $ExpectedUserSid `
        -ExpectedImagePath $ExpectedImagePath `
        -ExpectedSha256 $ExpectedSha256 `
        -ExpectedSigner $ExpectedSigner
    if ($CloseWindow -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(3000)) {
            $process.Kill()
            $process.WaitForExit()
        }
    }
    elseif (-not $process.WaitForExit(12000)) {
        $process.Kill()
        $process.WaitForExit()
    }
    $result = [ordered]@{
        evidence = $evidence
        exitCode = $process.ExitCode
        stdout = Read-TrimmedFile $stdout
        stderr = Read-TrimmedFile $stderr
    }
    Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
    return $result
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

$images = @($CliExe, $HostExe, $UiExe, $BrokerExe, $McpExe)
foreach ($path in $images) {
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

$signatureEvidence = foreach ($path in $images) {
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    [ordered]@{
        path = $path
        status = [string]$signature.Status
        signerThumbprint = if ($null -eq $signature.SignerCertificate) {
            $null
        }
        else {
            Get-SignerSha256 $signature
        }
        signerSubject = if ($null -eq $signature.SignerCertificate) {
            $null
        }
        else {
            $signature.SignerCertificate.Subject
        }
    }
}
$expectedSigner = if ([string]::IsNullOrWhiteSpace($SignerThumbprint)) {
    $signatureEvidence[0].signerThumbprint
}
else {
    $SignerThumbprint
}
$signaturesOk = @($signatureEvidence | Where-Object {
    $_.status -ne "Valid" -or
    -not [string]::Equals(
        $_.signerThumbprint,
        $expectedSigner,
        [StringComparison]::OrdinalIgnoreCase)
}).Count -eq 0
Write-StepJson `
    -Step "signature-precheck" `
    -ExitCode $(if ($signaturesOk) { 0 } else { 2 }) `
    -Ok $signaturesOk `
    -Output $signatureEvidence `
    -Error $(if ($signaturesOk) { "" } else { "All images must be validly signed by the same signer." })
if (-not $signaturesOk) {
    exit 2
}

$removeArgs = @("service", "uninstall")
if ($RemoveData) {
    $removeArgs += "--remove-data"
}

try {
    $vaultDbPath = Join-Path $dataRoot "Vault\vault.db"
    $vaultKeysPath = Join-Path $dataRoot "Vault\vault.keys"
    $initialDataHashes = [ordered]@{
        vaultDb = if (Test-Path -LiteralPath $vaultDbPath) {
            (Get-FileHash -LiteralPath $vaultDbPath -Algorithm SHA256).Hash
        }
        else {
            $null
        }
        vaultKeys = if (Test-Path -LiteralPath $vaultKeysPath) {
            (Get-FileHash -LiteralPath $vaultKeysPath -Algorithm SHA256).Hash
        }
        else {
            $null
        }
    }
    $currentService = Get-CimInstance Win32_Service `
        -Filter "Name='$serviceName'" `
        -ErrorAction SilentlyContinue
    if ($null -ne $currentService) {
        Invoke-ServiceStep -Step "baseline-status" -Arguments @("service", "status") | Out-Null
        Invoke-ServiceStep -Step "baseline-stop" -Arguments @("service", "stop") | Out-Null
        if (-not (Wait-ServiceState -ExpectedState "Stopped")) {
            throw "Service did not reach stopped before baseline uninstall."
        }
        Invoke-ServiceStep -Step "baseline-uninstall" -Arguments $removeArgs | Out-Null
        if (-not (Wait-ServiceState -ExpectedState "notInstalled")) {
            throw "Service was not removed before install."
        }
    }
    Stop-InstalledClientProcesses -ImagePaths @(
        $UiExe,
        $CliExe,
        $BrokerExe,
        $McpExe
    )

    $installArgs = @(
        "service", "install",
        "--host-exe", $HostExe,
        "--ui-exe", $UiExe,
        "--cli-exe", $CliExe,
        "--broker-exe", $BrokerExe,
        "--mcp-exe", $McpExe,
        "--signer-thumbprint", $expectedSigner,
        "--no-start"
    )
    Invoke-ServiceStep -Step "install" -Arguments $installArgs | Out-Null
    Invoke-ServiceStep -Step "start" -Arguments @("service", "start") | Out-Null
    if (-not (Wait-ServiceState -ExpectedState "Running")) {
        throw "Service did not reach running after start."
    }
    $status = Invoke-ServiceStep `
        -Step "status-after-start" `
        -Arguments @("service", "status")
    if (-not $status.installed -or $status.state -ne "running") {
        throw "Service did not report installed/running after start."
    }

    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    $initialInstallId = [string]$manifest.installId
    $acl = Get-Acl -LiteralPath $dataRoot
    $serviceSid = [string]$manifest.serviceSid
    $ownerSid = [string]$manifest.ownerSid
    $serviceAclRules = @($acl.Access | Where-Object {
        [string]::Equals(
            $_.IdentityReference.Translate(
                [Security.Principal.SecurityIdentifier]).Value,
            $serviceSid,
            [StringComparison]::OrdinalIgnoreCase)
    })
    $fullControl = [Security.AccessControl.FileSystemRights]::FullControl
    $aclEvidence = [ordered]@{
        owner = $acl.Owner
        ownerSid = (Get-AccountSid $acl.Owner)
        protected = $acl.AreAccessRulesProtected
        serviceRuleCount = $serviceAclRules.Count
        serviceHasFullControl = @($serviceAclRules | Where-Object {
            ($_.FileSystemRights -band $fullControl) -eq $fullControl
        }).Count -gt 0
        expectedServiceSid = $serviceSid
    }
    $aclOk =
        [string]::Equals(
            $aclEvidence.ownerSid,
            $serviceSid,
            [StringComparison]::OrdinalIgnoreCase) -and
        $aclEvidence.protected -and
        $aclEvidence.serviceHasFullControl
    $aclEvidence.ok = $aclOk
    Write-StepJson `
        -Step "data-acl" `
        -ExitCode $(if ($aclOk) { 0 } else { 1 }) `
        -Ok $aclOk `
        -Output $aclEvidence `
        -Error $(if ($aclOk) { "" } else { "ProgramData owner or DACL does not match the service SID." })
    if (-not $aclOk) {
        throw "ProgramData ACL verification failed."
    }

    $runningService = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($runningService.State -ne "Running" -or
        $runningService.StartName -ne $manifest.serviceAccount) {
        throw "SCM service state or service account is invalid."
    }
    $hostImage = Get-ManifestImage -Manifest $manifest -Kind "HostService"
    $hostEvidence = Get-ProcessEvidence `
        -ProcessId ([int]$runningService.ProcessId) `
        -ExpectedUserSid $serviceSid `
        -ExpectedImagePath ([string]$hostImage.path) `
        -ExpectedSha256 ([string]$hostImage.sha256) `
        -ExpectedSigner $expectedSigner
    Write-StepJson `
        -Step "host-process-identity" `
        -ExitCode $(if ($hostEvidence.ok) { 0 } else { 1 }) `
        -Ok $hostEvidence.ok `
        -Output $hostEvidence `
        -Error $(if ($hostEvidence.ok) { "" } else { "Host process identity verification failed." })
    if (-not $hostEvidence.ok) {
        throw "Host process identity verification failed."
    }

    $cliImage = Get-ManifestImage -Manifest $manifest -Kind "AdminCli"
    $cliRun = Start-ProcessAndInspect `
        -FilePath $CliExe `
        -Arguments @("status") `
        -ExpectedUserSid $ownerSid `
        -ExpectedImagePath ([string]$cliImage.path) `
        -ExpectedSha256 ([string]$cliImage.sha256) `
        -ExpectedSigner $expectedSigner
    $cliStatusOutput = & $CliExe status 2>&1 | Out-String
    $cliStatusExitCode = $LASTEXITCODE
    $cliStatusJson = $null
    try {
        $cliStatusJson = $cliStatusOutput | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
    }
    $cliRun.verifiedExitCode = $cliStatusExitCode
    $cliRun.verifiedOutput = $cliStatusOutput.Trim()
    $cliOk =
        $cliRun.evidence.ok -and
        $cliStatusExitCode -eq 0 -and
        $null -ne $cliStatusJson -and
        $cliStatusJson.managedByService -eq $true
    Write-StepJson `
        -Step "cli-process-identity" `
        -ExitCode $(if ($cliOk) { 0 } else { 1 }) `
        -Ok $cliOk `
        -Output $cliRun `
        -Error $(if ($cliOk) { "" } else { "CLI process or status RPC verification failed." })
    if (-not $cliOk) {
        throw "CLI process or status RPC verification failed."
    }

    $brokerImage = Get-ManifestImage -Manifest $manifest -Kind "AuthBroker"
    $brokerProcess = Get-CimInstance Win32_Process |
        Where-Object {
            [string]::Equals(
                $_.ExecutablePath,
                [string]$brokerImage.path,
                [StringComparison]::OrdinalIgnoreCase)
        } |
        Sort-Object CreationDate -Descending |
        Select-Object -First 1
    if ($null -eq $brokerProcess) {
        throw "Auth Broker process was not found after CLI status."
    }
    $brokerEvidence = Get-ProcessEvidence `
        -ProcessId ([int]$brokerProcess.ProcessId) `
        -ExpectedUserSid $ownerSid `
        -ExpectedImagePath ([string]$brokerImage.path) `
        -ExpectedSha256 ([string]$brokerImage.sha256) `
        -ExpectedSigner $expectedSigner
    Write-StepJson `
        -Step "broker-process-identity" `
        -ExitCode $(if ($brokerEvidence.ok) { 0 } else { 1 }) `
        -Ok $brokerEvidence.ok `
        -Output $brokerEvidence `
        -Error $(if ($brokerEvidence.ok) { "" } else { "Broker process identity verification failed." })
    if (-not $brokerEvidence.ok) {
        throw "Broker process identity verification failed."
    }

    $uiImage = Get-ManifestImage -Manifest $manifest -Kind "AdminUi"
    $uiRun = Start-ProcessAndInspect `
        -FilePath $UiExe `
        -Arguments @() `
        -ExpectedUserSid $ownerSid `
        -ExpectedImagePath ([string]$uiImage.path) `
        -ExpectedSha256 ([string]$uiImage.sha256) `
        -ExpectedSigner $expectedSigner `
        -CloseWindow
    Write-StepJson `
        -Step "ui-process-identity" `
        -ExitCode $(if ($uiRun.evidence.ok) { 0 } else { 1 }) `
        -Ok $uiRun.evidence.ok `
        -Output $uiRun.evidence `
        -Error $(if ($uiRun.evidence.ok) { "" } else { "UI process identity verification failed." })
    if (-not $uiRun.evidence.ok) {
        throw "UI process identity verification failed."
    }

    $mcpImage = Get-ManifestImage -Manifest $manifest -Kind "McpBridge"
    $missingManifestPath = Join-Path $env:TEMP "cipher-vault-smoke-$([guid]::NewGuid().ToString('N')).json"
    $missingManifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    $missingManifest.pipes.bridge = "CipherVault.Smoke.Unavailable.$PID"
    $missingManifest |
        ConvertTo-Json -Depth 30 |
        Set-Content -LiteralPath $missingManifestPath -Encoding utf8
    try {
        $mcpRun = Start-ProcessAndInspect `
            -FilePath $McpExe `
            -Arguments @("--manifest", $missingManifestPath, "--scopes", "read") `
            -ExpectedUserSid $ownerSid `
            -ExpectedImagePath ([string]$mcpImage.path) `
            -ExpectedSha256 ([string]$mcpImage.sha256) `
            -ExpectedSigner $expectedSigner `
            -InspectWithinMilliseconds 2000
    }
    finally {
        Remove-Item -LiteralPath $missingManifestPath -Force -ErrorAction SilentlyContinue
    }
    Write-StepJson `
        -Step "mcp-process-identity" `
        -ExitCode $(if ($mcpRun.evidence.ok) { 0 } else { 1 }) `
        -Ok $mcpRun.evidence.ok `
        -Output $mcpRun `
        -Error $(if ($mcpRun.evidence.ok) { "" } else { "MCP process identity verification failed." })
    if (-not $mcpRun.evidence.ok) {
        throw "MCP process identity verification failed."
    }

    Invoke-ServiceStep -Step "stop" -Arguments @("service", "stop") | Out-Null
    if (-not (Wait-ServiceState -ExpectedState "Stopped")) {
        throw "Service did not reach stopped after stop."
    }
    $stopped = Invoke-ServiceStep `
        -Step "status-after-stop" `
        -Arguments @("service", "status")
    if ($stopped.state -ne "stopped") {
        throw "Service did not report stopped after stop."
    }
    Invoke-ServiceStep -Step "uninstall" -Arguments $removeArgs | Out-Null
    if (-not (Wait-ServiceState -ExpectedState "notInstalled")) {
        throw "Service was not removed after uninstall."
    }
    $afterUninstall = & $CliExe service status 2>&1 | Out-String
    $afterUninstallExitCode = $LASTEXITCODE
    $uninstalledOk = $afterUninstallExitCode -ne 0
    Write-StepJson `
        -Step "status-after-uninstall" `
        -ExitCode $(if ($uninstalledOk) { 0 } else { 1 }) `
        -Ok $uninstalledOk `
        -Output ($afterUninstall.Trim()) `
        -Error $(if ($uninstalledOk) { "" } else { "Service is still installed after uninstall." })
    if (-not $uninstalledOk) {
        throw "Service is still installed after uninstall."
    }
    Stop-InstalledClientProcesses -ImagePaths @(
        $UiExe,
        $CliExe,
        $BrokerExe,
        $McpExe
    )

    Invoke-ServiceStep -Step "reinstall" -Arguments $installArgs | Out-Null
    Invoke-ServiceStep -Step "reinstall-start" -Arguments @("service", "start") | Out-Null
    if (-not (Wait-ServiceState -ExpectedState "Running")) {
        throw "Service did not reach running after reinstall."
    }
    $reinstalled = Invoke-ServiceStep `
        -Step "reinstall-status" `
        -Arguments @("service", "status")
    if (-not $reinstalled.installed -or $reinstalled.state -ne "running") {
        throw "Service did not report installed/running after reinstall."
    }
    $reinstalledManifest = Get-Content -Raw -LiteralPath $manifestPath |
        ConvertFrom-Json
    $reinstalledInstallId = [string]$reinstalledManifest.installId
    $currentDataHashes = [ordered]@{
        vaultDb = if (Test-Path -LiteralPath $vaultDbPath) {
            (Get-FileHash -LiteralPath $vaultDbPath -Algorithm SHA256).Hash
        }
        else {
            $null
        }
        vaultKeys = if (Test-Path -LiteralPath $vaultKeysPath) {
            (Get-FileHash -LiteralPath $vaultKeysPath -Algorithm SHA256).Hash
        }
        else {
            $null
        }
    }
    $dataPreserved =
        [string]::Equals(
            [string]$initialDataHashes.vaultDb,
            [string]$currentDataHashes.vaultDb,
            [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals(
            [string]$initialDataHashes.vaultKeys,
            [string]$currentDataHashes.vaultKeys,
            [StringComparison]::OrdinalIgnoreCase)
    $reinstallEvidence = [ordered]@{
        initialInstallId = $initialInstallId
        reinstalledInstallId = $reinstalledInstallId
        installIdChanged = -not [string]::Equals(
            $initialInstallId,
            $reinstalledInstallId,
            [StringComparison]::OrdinalIgnoreCase)
        dataPreserved = $dataPreserved
        initialDataHashes = $initialDataHashes
        currentDataHashes = $currentDataHashes
    }
    $reinstallEvidenceOk =
        $reinstallEvidence.installIdChanged -and
        ($RemoveData -or $reinstallEvidence.dataPreserved)
    Write-StepJson `
        -Step "reinstall-evidence" `
        -ExitCode $(if ($reinstallEvidenceOk) { 0 } else { 1 }) `
        -Ok $reinstallEvidenceOk `
        -Output $reinstallEvidence `
        -Error $(if ($reinstallEvidenceOk) { "" } else { "Reinstall did not produce a new install ID or changed Vault data." })
    if (-not $reinstallEvidenceOk) {
        throw "Reinstall evidence verification failed."
    }
}
catch {
    Write-StepJson `
        -Step "smoke" `
        -ExitCode 1 `
        -Ok $false `
        -Output $null `
        -Error $_.Exception.Message
    $events = Get-WinEvent -FilterHashtable @{
        LogName = "System"
        StartTime = $startedAt
        Id = @(7000, 7009, 7031, 7034, 7045)
    } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match "CipherVault" } |
        Select-Object -First 10 |
        ForEach-Object {
            [ordered]@{
                timeCreated = $_.TimeCreated
                id = $_.Id
                level = [string]$_.LevelDisplayName
                message = ($_.Message -replace "`r?`n", " ")
            }
        }
    Write-StepJson `
        -Step "service-events" `
        -ExitCode 1 `
        -Ok $false `
        -Output $events `
        -Error ""
    try {
        $service = Get-CimInstance Win32_Service `
            -Filter "Name='$serviceName'" `
            -ErrorAction SilentlyContinue
        if ($null -ne $service) {
            $null = & $CliExe service stop 2>&1
            $null = & $CliExe service uninstall 2>&1
        }
        Stop-InstalledClientProcesses -ImagePaths @(
            $UiExe,
            $CliExe,
            $BrokerExe,
            $McpExe
        )
        if (Test-Path -LiteralPath $manifestPath) {
            $null = & $CliExe service install `
                --host-exe $HostExe `
                --ui-exe $UiExe `
                --cli-exe $CliExe `
                --broker-exe $BrokerExe `
                --mcp-exe $McpExe `
                --signer-thumbprint $expectedSigner `
                2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "Cleanup reinstall failed with exit code $LASTEXITCODE."
            }
            $null = & $CliExe service start 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "Cleanup service start failed with exit code $LASTEXITCODE."
            }
        }
    }
    catch {
        Write-StepJson `
            -Step "cleanup" `
            -ExitCode 1 `
            -Ok $false `
            -Output $null `
            -Error $_.Exception.Message
    }
    exit 1
}
