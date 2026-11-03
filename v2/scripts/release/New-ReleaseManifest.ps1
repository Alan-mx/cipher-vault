[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ReleaseRoot,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Commit,
    [Parameter(Mandatory)] [DateTimeOffset] $BuiltAtUtc,
    [Parameter(Mandatory)] [string] $RuntimeVersion,
    [Parameter(Mandatory)] [string] $AspNetCoreVersion,
    [Parameter(Mandatory)] [string] $SdkVersion,
    [Parameter(Mandatory)] [string] $Sqlite3McVersion,
    [Parameter(Mandatory)] [string] $DockerBaseImage,
    [Parameter(Mandatory)] [string] $DockerBaseImageDigest
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $ReleaseRoot).Path
if ($Version -notmatch '^2\.0\.0-rc\.[0-9]+$') {
    throw "release_version_invalid"
}
if ($Commit -notmatch '^[0-9a-f]{40}$') {
    throw "release_commit_invalid"
}
if ($DockerBaseImageDigest -notmatch '^sha256:[0-9a-f]{64}$') {
    throw "release_docker_digest_invalid"
}
$BuiltAtUtc = $BuiltAtUtc.ToUniversalTime()
$artifacts = @(
    @{
        Kind = "windows-zip"
        FileName = "CipherVault-$Version-win-x64.zip"
        Rid = "win-x64"
    },
    @{
        Kind = "linux-targz"
        FileName = "CipherVault-$Version-linux-x64.tar.gz"
        Rid = "linux-x64"
    },
    @{
        Kind = "linux-targz"
        FileName = "CipherVault-$Version-linux-arm64.tar.gz"
        Rid = "linux-arm64"
    },
    @{
        Kind = "docker-image-tar"
        FileName = "cipher-vault-$Version-docker-linux-amd64.tar"
        Rid = $null
    }
)

function Get-HashAndSize {
    param([string] $Path)
    $file = Get-Item -LiteralPath $Path
    [pscustomobject]@{
        Bytes = [long]$file.Length
        Sha256 = (
            (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        ).ToLowerInvariant()
    }
}

$entries = foreach ($artifact in $artifacts) {
    $path = Join-Path $root $artifact.FileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "artifact_missing:$($artifact.FileName)"
    }
    $hash = Get-HashAndSize $path
    [ordered]@{
        kind = $artifact.Kind
        fileName = $artifact.FileName
        bytes = $hash.Bytes
        sha256 = $hash.Sha256
        rid = $artifact.Rid
    }
}

$manifest = [ordered]@{
    product = "CipherVault"
    version = $Version
    commit = $Commit
    builtAtUtc = (
        $BuiltAtUtc.ToUniversalTime()
    ).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
    runtimeVersion = $RuntimeVersion
    aspNetCoreVersion = $AspNetCoreVersion
    sdkVersion = $SdkVersion
    sqlite3McVersion = $Sqlite3McVersion
    dockerBaseImage = $DockerBaseImage
    dockerBaseImageDigest = $DockerBaseImageDigest
    artifacts = @($entries)
}
$manifestPath = Join-Path $root "release-manifest.json"
$json = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText(
    $manifestPath,
    $json,
    [Text.UTF8Encoding]::new($false))

$checksumFiles = [string[]] @(
    $artifacts.FileName
    "release-manifest.json"
)
[Array]::Sort($checksumFiles, [StringComparer]::Ordinal)
$lines = foreach ($name in $checksumFiles) {
    $path = Join-Path $root $name
    $hash = Get-HashAndSize $path
    "$($hash.Sha256)  $name"
}
$checksumPath = Join-Path $root "SHA256SUMS"
[IO.File]::WriteAllLines(
    $checksumPath,
    [string[]]$lines,
    [Text.UTF8Encoding]::new($false))

$parsed = Get-Content -LiteralPath $manifestPath -Raw |
    ConvertFrom-Json -Depth 12
if ($parsed.Product -ne "CipherVault" -or
    $parsed.Version -ne $Version -or
    $parsed.Commit -ne $Commit -or
    @($parsed.Artifacts).Count -ne 4) {
    throw "release_manifest_validation_failed"
}
$expectedArtifactIdentity = @(
    @(
        "CipherVault-$Version-win-x64.zip",
        "win-x64"
    ),
    @(
        "CipherVault-$Version-linux-x64.tar.gz",
        "linux-x64"
    ),
    @(
        "CipherVault-$Version-linux-arm64.tar.gz",
        "linux-arm64"
    ),
    @(
        "cipher-vault-$Version-docker-linux-amd64.tar",
        $null
    )
)
for ($index = 0; $index -lt $expectedArtifactIdentity.Count; $index++) {
    $expected = $expectedArtifactIdentity[$index]
    $actual = $parsed.Artifacts[$index]
    if ($actual.fileName -ne $expected[0] -or
        $actual.rid -ne $expected[1]) {
        throw "release_manifest_artifact_identity_invalid:$index"
    }
}
foreach ($artifact in $parsed.Artifacts) {
    $path = Join-Path $root $artifact.fileName
    $actual = Get-HashAndSize $path
    if ([long]$artifact.bytes -ne $actual.Bytes -or
        $artifact.sha256 -ne $actual.Sha256) {
        throw "release_manifest_hash_mismatch"
    }
}

Write-Output $manifestPath
