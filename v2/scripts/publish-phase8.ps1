[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("win-x64", "linux-x64", "linux-arm64")]
    [string] $Rid,
    [Parameter(Mandatory)]
    [string] $OutputRoot,
    [string] $Version = "",
    [string] $Commit = "",
    [switch] $SkipFrontendBuild
)

$ErrorActionPreference = "Stop"
$v2Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$repoRoot = (Resolve-Path (Join-Path $v2Root "..")).Path
$solution = Join-Path $repoRoot "CipherVault.slnx"
. (Join-Path $v2Root "deploy/versions.ps1")

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = if ($env:CIPHERVAULT_RELEASE_VERSION) {
        $env:CIPHERVAULT_RELEASE_VERSION
    }
    else {
        $CipherVaultReleaseVersion
    }
}
if ($Version -notmatch '^2\.0\.0-rc\.[0-9]+$') {
    throw "release_version_invalid"
}

Push-Location $repoRoot
try {
    if (git status --porcelain --untracked-files=no) {
        throw "release_worktree_dirty"
    }
    if ([string]::IsNullOrWhiteSpace($Commit)) {
        $Commit = if ($env:CIPHERVAULT_BUILD_COMMIT) {
            $env:CIPHERVAULT_BUILD_COMMIT
        }
        else {
            (git rev-parse HEAD).Trim()
        }
    }
}
finally {
    Pop-Location
}
if ($Commit -notmatch '^[0-9a-f]{40}$') {
    throw "release_commit_invalid"
}

$timestamp = if ($env:CIPHERVAULT_BUILD_TIMESTAMP_UTC) {
    ([DateTimeOffset]::Parse($env:CIPHERVAULT_BUILD_TIMESTAMP_UTC)).ToUniversalTime()
}
else {
    [DateTimeOffset]::UtcNow
}
$timestampText = $timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
$output = [IO.Path]::GetFullPath($OutputRoot)
$releaseBase = [IO.Path]::GetFullPath(
    (Join-Path $v2Root "artifacts/release/$Version"))
$relativeOutput = [IO.Path]::GetRelativePath($releaseBase, $output)
if ([IO.Path]::IsPathRooted($relativeOutput) -or
    $relativeOutput -eq "." -or
    $relativeOutput.StartsWith(
        "..$([IO.Path]::DirectorySeparatorChar)",
        [StringComparison]::Ordinal) -or
    $relativeOutput -eq "..") {
    throw "release_output_root_invalid"
}
if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
Microsoft.PowerShell.Management\New-Item `
    -ItemType Directory `
    -Path $output `
    -Force | Out-Null

if (-not $SkipFrontendBuild) {
    Push-Location (Join-Path $v2Root "frontend")
    try {
        npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "frontend_npm_ci_failed" }
        npm run format:check
        if ($LASTEXITCODE -ne 0) { throw "frontend_format_failed" }
        npm run typecheck
        if ($LASTEXITCODE -ne 0) { throw "frontend_typecheck_failed" }
        npm run lint
        if ($LASTEXITCODE -ne 0) { throw "frontend_lint_failed" }
        npm test
        if ($LASTEXITCODE -ne 0) { throw "frontend_test_failed" }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "frontend_build_failed" }
    }
    finally {
        Pop-Location
    }
}

$publishRoot = Join-Path $output "publish"
Push-Location $v2Root
try {
    dotnet restore $solution --locked-mode -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "release_restore_failed" }
    dotnet publish src/CipherVault.Server/CipherVault.Server.csproj `
        -c Release `
        -r $Rid `
        --self-contained true `
        --no-restore `
        -warnaserror `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:CipherVaultReleaseVersion=$Version `
        -p:CipherVaultBuildCommit=$Commit `
        -p:CipherVaultBuildTimestampUtc=$timestampText `
        -o $publishRoot
    if ($LASTEXITCODE -ne 0) { throw "release_publish_failed" }
}
finally {
    Pop-Location
}

& (Join-Path $v2Root "scripts/release/Test-PublishLayout.ps1") `
    -PublishRoot $publishRoot `
    -Rid $Rid
if ($LASTEXITCODE -ne 0) { throw "release_publish_layout_invalid" }

Write-Output $publishRoot
