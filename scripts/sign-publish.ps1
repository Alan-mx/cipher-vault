param(
    [string]$StagePath = "$PSScriptRoot\..\artifacts\publish\CipherVault-win-x64",
    [string]$ArchivePath = "$PSScriptRoot\..\artifacts\publish\CipherVault-win-x64.zip",
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9A-Fa-f]{40}$")]
    [string]$SignerThumbprint,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$SigntoolPath
)

$ErrorActionPreference = "Stop"

$stage = (Resolve-Path -LiteralPath $StagePath).Path
$archiveParent = Split-Path -Parent $ArchivePath
$archiveDirectory = (Resolve-Path -LiteralPath $archiveParent).Path
$archiveName = Split-Path -Leaf $ArchivePath
$fullArchivePath = Join-Path $archiveDirectory $archiveName

if (-not (Test-Path -LiteralPath $stage)) {
    throw "Publish stage was not found: $stage"
}

$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object {
        [string]::Equals(
            $_.Thumbprint,
            $SignerThumbprint,
            [StringComparison]::OrdinalIgnoreCase)
    } |
    Select-Object -First 1
if ($null -eq $certificate) {
    throw "Code signing certificate was not found in CurrentUser\My: $SignerThumbprint"
}
if (-not $certificate.HasPrivateKey) {
    throw "Code signing certificate does not have a private key: $SignerThumbprint"
}

if ([string]::IsNullOrWhiteSpace($SigntoolPath)) {
    $kitRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    $kit = Get-ChildItem -LiteralPath $kitRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Where-Object { Test-Path (Join-Path $_.FullName "x64\signtool.exe") } |
        Select-Object -First 1
    if ($null -ne $kit) {
        $SigntoolPath = Join-Path $kit.FullName "x64\signtool.exe"
    }
}

if (-not [string]::IsNullOrWhiteSpace($SigntoolPath) -and
    -not (Test-Path -LiteralPath $SigntoolPath)) {
    throw "signtool.exe was not found: $SigntoolPath"
}

$executables = Get-ChildItem -LiteralPath $stage -Filter "*.exe" -File
if ($executables.Count -eq 0) {
    throw "No executables were found in publish stage: $stage"
}

foreach ($executable in $executables) {
    if (-not [string]::IsNullOrWhiteSpace($SigntoolPath)) {
        & $SigntoolPath sign `
            /sha1 $SignerThumbprint `
            /fd SHA256 `
            /tr $TimestampUrl `
            /td SHA256 `
            /q `
            $executable.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "Signing failed: $($executable.FullName)"
        }
    }
    else {
        $signature = Set-AuthenticodeSignature `
            -FilePath $executable.FullName `
            -Certificate $certificate `
            -HashAlgorithm SHA256 `
            -TimestampServer $TimestampUrl
        if ($signature.Status -ne "Valid") {
            throw "Signing failed: $($executable.FullName): $($signature.StatusMessage)"
        }
    }

    $verified = Get-AuthenticodeSignature -LiteralPath $executable.FullName
    if ($verified.Status -ne "Valid") {
        throw "Signature verification failed: $($executable.FullName): $($verified.StatusMessage)"
    }
}

if (Test-Path -LiteralPath $fullArchivePath) {
    Remove-Item -LiteralPath $fullArchivePath -Force
}

$stageParent = Split-Path -Parent $stage
$stageName = Split-Path -Leaf $stage
tar -a -cf $fullArchivePath -C $stageParent $stageName
if ($LASTEXITCODE -ne 0) {
    throw "Archive creation failed with exit code $LASTEXITCODE"
}

Write-Host "Signed stage: $stage"
Write-Host "Signed ZIP: $fullArchivePath"
