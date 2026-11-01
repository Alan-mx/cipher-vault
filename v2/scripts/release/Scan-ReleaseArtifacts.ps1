[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ReleaseRoot,
    [Parameter(Mandatory)] [string] $EvidencePath,
    [string] $Version = "2.0.0-rc.1",
    [string[]] $ForbiddenMarkers = @(
        "phase8-release-password-marker",
        "phase8-release-token-marker",
        "phase8-release-content-marker"
    )
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $ReleaseRoot).Path
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) `
    "cipher-vault-scan-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
$findings = [System.Collections.Generic.List[string]]::new()
$scannedRoots = [System.Collections.Generic.List[string]]::new()
$checksumFiles = Get-ChildItem -LiteralPath $root -File |
    Where-Object { $_.Name -in "release-manifest.json", "SHA256SUMS" }

function Add-Finding {
    param([string] $Code, [string] $Path)
    $relative = [IO.Path]::GetRelativePath($root, $Path)
    $findings.Add("$Code`:$relative")
}

function Test-FileMarkers {
    param([string] $Path)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $buffer = New-Object byte[] 65536
        $overlap = ""
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $text = $overlap + [Text.Encoding]::ASCII.GetString($buffer, 0, $read)
            for ($markerIndex = 0;
                $markerIndex -lt $ForbiddenMarkers.Count;
                $markerIndex++) {
                $marker = $ForbiddenMarkers[$markerIndex]
                if ($text.Contains($marker, [StringComparison]::Ordinal)) {
                    Add-Finding "forbidden_marker:$markerIndex" $Path
                    return
                }
            }
            if ($text -match "raw:[0-9A-Fa-f]{64}") {
                Add-Finding "raw_key_material" $Path
                return
            }
            $overlapLength = [Math]::Min(128, $text.Length)
            $overlap = $text.Substring($text.Length - $overlapLength)
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Scan-Tree {
    param([string] $TreeRoot, [string] $Label)
    $scannedRoots.Add($Label)
    $forbiddenExtensions = @(".cs", ".csproj", ".slnx", ".pdb")
    foreach ($file in Get-ChildItem -LiteralPath $TreeRoot -Recurse -File) {
        if ($file.Extension -in $forbiddenExtensions -or
            $file.Name -eq "appsettings.Development.json" -or
            $file.Extension -in @(".tmp", ".partial", ".log")) {
            Add-Finding "forbidden_file" $file.FullName
        }
        Test-FileMarkers $file.FullName
    }
}

function Scan-MetadataTree {
    param([string] $TreeRoot, [string] $Label)
    $scannedRoots.Add($Label)
    $archiveNames = @(
        "CipherVault-$Version-win-x64.zip",
        "CipherVault-$Version-linux-x64.tar.gz",
        "cipher-vault-$Version-docker-linux-amd64.tar"
    )
    foreach ($file in Get-ChildItem -LiteralPath $TreeRoot -Recurse -File |
        Where-Object { $_.Name -notin $archiveNames }) {
        Test-FileMarkers $file.FullName
    }
}

try {
    $zip = Join-Path $root "CipherVault-$Version-win-x64.zip"
    $tar = Join-Path $root "CipherVault-$Version-linux-x64.tar.gz"
    $dockerTar = Join-Path $root "cipher-vault-$Version-docker-linux-amd64.tar"
    $allowedTopLevel = @(
        [IO.Path]::GetFileName($zip),
        [IO.Path]::GetFileName($tar),
        [IO.Path]::GetFileName($dockerTar),
        "release-manifest.json",
        "SHA256SUMS",
        "evidence",
        "logs"
    )
    foreach ($entry in Get-ChildItem -LiteralPath $root -Force) {
        if ($entry.Name -notin $allowedTopLevel) {
            Add-Finding "unexpected_release_entry" $entry.FullName
        }
    }
    foreach ($path in @($zip, $tar, $dockerTar)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Add-Finding "artifact_missing" $path
        }
    }

    if (Test-Path -LiteralPath $zip) {
        $zipRoot = Join-Path $temporaryRoot "windows"
        Microsoft.PowerShell.Archive\Expand-Archive `
            -LiteralPath $zip `
            -DestinationPath $zipRoot
        Scan-Tree $zipRoot "windows-zip"
    }
    if (Test-Path -LiteralPath $tar) {
        $tarRoot = Join-Path $temporaryRoot "linux"
        New-Item -ItemType Directory -Path $tarRoot -Force | Out-Null
        & tar -xzf $tar -C $tarRoot
        if ($LASTEXITCODE -ne 0) {
            Add-Finding "archive_extract_failed" $tar
        }
        else {
            Scan-Tree $tarRoot "linux-targz"
        }
    }
    if (Test-Path -LiteralPath $dockerTar) {
        Test-FileMarkers $dockerTar
        $dockerEntries = & tar -tf $dockerTar
        if ($LASTEXITCODE -ne 0) {
            Add-Finding "docker_tar_inspection_failed" $dockerTar
        }
        else {
            foreach ($entry in $dockerEntries) {
                if ($entry -match '(^|/)\.git(/|$)' -or
                    $entry -match 'appsettings\.Development\.json$') {
                    Add-Finding "docker_tar_forbidden_entry" $dockerTar
                    break
                }
            }
        }
        $scannedRoots.Add("docker-image-tar")
    }
    foreach ($file in $checksumFiles) {
        Test-FileMarkers $file.FullName
    }
    Scan-MetadataTree $root "release-metadata-and-evidence"

    $evidence = [ordered]@{
        passed = $findings.Count -eq 0
        scannedRoots = @($scannedRoots)
        forbiddenPatterns = @(
            $ForbiddenMarkers | ForEach-Object {
                $bytes = [Text.Encoding]::UTF8.GetBytes($_)
                $hash = [Security.Cryptography.SHA256]::HashData($bytes)
                "marker_sha256:$([Convert]::ToHexString($hash).ToLowerInvariant())"
            }
        )
        findings = @($findings)
        scannedAtUtc = (
            [DateTimeOffset]::UtcNow
        ).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
    }
    $evidenceDirectory = Split-Path -Parent $EvidencePath
    New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
    [IO.File]::WriteAllText(
        $EvidencePath,
        ($evidence | ConvertTo-Json -Depth 6),
        [Text.UTF8Encoding]::new($false))
    if ($findings.Count -ne 0) {
        throw "release_scan_failed"
    }
    Write-Output "release_scan_passed"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
