$versionFile = Join-Path $PSScriptRoot "versions.env"
if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
    throw "versions.env is required."
}

$versions = @{}
foreach ($line in Get-Content -LiteralPath $versionFile) {
    if ($line -match '^\s*#' -or [string]::IsNullOrWhiteSpace($line)) {
        continue
    }
    if ($line -notmatch '^([A-Z0-9_]+)=([^\r\n]+)$') {
        throw "versions.env contains an invalid line."
    }
    $versions[$Matches[1]] = $Matches[2]
}

function Get-RequiredVersionValue([string] $name) {
    $value = $versions[$name]
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "versions.env is missing $name."
    }
    return $value
}

$CipherVaultReleaseVersion = Get-RequiredVersionValue "RELEASE_VERSION"
$CipherVaultDotnetSdkVersion = Get-RequiredVersionValue "DOTNET_SDK_VERSION"
$CipherVaultDotnetRuntimeVersion = Get-RequiredVersionValue "DOTNET_RUNTIME_VERSION"
$CipherVaultAspNetCoreVersion = Get-RequiredVersionValue "DOTNET_ASPNETCORE_VERSION"
$CipherVaultSqlite3McVersion = Get-RequiredVersionValue "SQLITE3MC_VERSION"
$CipherVaultDockerBaseImage = "mcr.microsoft.com/dotnet/runtime-deps:$(Get-RequiredVersionValue "DOTNET_RUNTIME_DEPS_TAG")"
$CipherVaultDockerBaseDigest = Get-RequiredVersionValue "DOTNET_RUNTIME_DEPS_DIGEST"
$CipherVaultDockerSdkImage = "mcr.microsoft.com/dotnet/sdk:$CipherVaultDotnetSdkVersion-noble"
$CipherVaultDockerSdkDigest = Get-RequiredVersionValue "DOTNET_SDK_IMAGE_DIGEST"
