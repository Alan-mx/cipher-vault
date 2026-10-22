param(
    [string]$StagePath = "$PSScriptRoot\..\artifacts\publish\CipherVault-win-x64",
    [string]$ArchivePath = "$PSScriptRoot\..\artifacts\publish\CipherVault-win-x64.zip",
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9A-Fa-f]{64}$")]
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

if ([string]::IsNullOrWhiteSpace($SigntoolPath)) {
    $kitRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    $kit = Get-ChildItem -LiteralPath $kitRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Where-Object { Test-Path (Join-Path $_.FullName "x64\signtool.exe") } |
        Select-Object -First 1
    if ($null -eq $kit) {
        throw "signtool.exe was not found. Install the Windows SDK or set SigntoolPath."
    }
    $SigntoolPath = Join-Path $kit.FullName "x64\signtool.exe"
}

if (-not (Test-Path -LiteralPath $SigntoolPath)) {
    throw "signtool.exe was not found: $SigntoolPath"
}

$executables = Get-ChildItem -LiteralPath $stage -Filter "*.exe" -File
if ($executables.Count -eq 0) {
    throw "No executables were found in publish stage: $stage"
}

foreach ($executable in $executables) {
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

    & $SigntoolPath verify /pa /q $executable.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "Signature verification failed: $($executable.FullName)"
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
