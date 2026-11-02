[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../..")).Path
$versionsPath = Join-Path $repoRoot "v2/deploy/versions.env"
$versions = @{}
foreach ($line in Get-Content -LiteralPath $versionsPath) {
    if ($line -match '^\s*#' -or [string]::IsNullOrWhiteSpace($line)) {
        continue
    }
    if ($line -notmatch '^([A-Z0-9_]+)=([^\r\n]+)$') {
        throw "versions_env_invalid"
    }
    $versions[$Matches[1]] = $Matches[2]
}

$releaseVersion = $versions["RELEASE_VERSION"]
if ($releaseVersion -ne "2.0.0-rc.1") {
    throw "release_version_gate_mismatch"
}

$workflow = Get-Content -Raw (
    Join-Path $repoRoot ".github/workflows/phase8-release.yml")
foreach ($required in @(
    'tags:',
    '- "v2.0.0-rc.1"',
    'GITHUB_REF_TYPE',
    'GITHUB_REF_PROTECTED',
    'GITHUB_REF_NAME',
    'artifactRoot=',
    'needs.prepare.outputs.commit'
)) {
    if (-not $workflow.Contains($required, [StringComparison]::Ordinal)) {
        throw "workflow_release_gate_missing:$required"
    }
}
if ($workflow.Contains('v2.0.0-rc.*', [StringComparison]::Ordinal)) {
    throw "workflow_accepts_unbounded_release_tags"
}

$versionsScript = Get-Content -Raw (
    Join-Path $repoRoot "v2/deploy/versions.ps1")
if ($versionsScript.Contains(
        '2.0.0-rc.1',
        [StringComparison]::Ordinal)) {
    throw "versions_ps1_contains_duplicate_release_version"
}

foreach ($relativePath in @(
    "v2/scripts/verify-phase8.ps1",
    "v2/scripts/verify-phase8.sh",
    "v2/scripts/verify-phase8-docker.sh",
    "v2/scripts/release/Scan-ReleaseArtifacts.ps1"
)) {
    $content = Get-Content -Raw (Join-Path $repoRoot $relativePath)
    if ($content.Contains(
            '2.0.0-rc.1',
            [StringComparison]::Ordinal)) {
        throw "release_script_hardcodes_version:$relativePath"
    }
}

$compose = Get-Content -Raw (
    Join-Path $repoRoot "v2/deploy/docker-compose.yml")
if ($compose.Contains(
        'cipher-vault:2.0.0-rc.1',
        [StringComparison]::Ordinal)) {
    throw "compose_hardcodes_release_image"
}

Write-Output "release-version-consistency-ok"
