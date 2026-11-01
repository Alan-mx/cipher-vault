[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("windows")]
    [string] $Platform,
    [Parameter(Mandatory)]
    [string] $PublishRoot,
    [Parameter(Mandatory)]
    [string] $OutputRoot,
    [string] $Version = ""
)

$ErrorActionPreference = "Stop"
$v2Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $v2Root "deploy/versions.ps1")
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $CipherVaultReleaseVersion
}

$publish = (Resolve-Path -LiteralPath $PublishRoot).Path
$output = [IO.Path]::GetFullPath($OutputRoot)
Microsoft.PowerShell.Management\New-Item `
    -ItemType Directory `
    -Path $output `
    -Force | Out-Null
$archive = Join-Path $output "CipherVault-$Version-win-x64.zip"
$staging = Join-Path ([IO.Path]::GetTempPath()) `
    "cipher-vault-package-$([Guid]::NewGuid().ToString('N'))"
$wrapperName = "CipherVault-$Version-win-x64"
$wrapper = Join-Path $staging $wrapperName

try {
    Microsoft.PowerShell.Management\New-Item `
        -ItemType Directory `
        -Path $wrapper `
        -Force | Out-Null
    Copy-Item -Path (Join-Path $publish "*") -Destination $wrapper -Recurse
    Copy-Item -LiteralPath (Join-Path $v2Root "deploy/windows/Start-CipherVault.ps1") $wrapper
    Copy-Item -LiteralPath (Join-Path $v2Root "deploy/windows/Install-CipherVaultStartup.ps1") $wrapper
    Copy-Item -LiteralPath (Join-Path $v2Root "deploy/windows/README.md") `
        (Join-Path $wrapper "README.md")

    $forbidden = Get-ChildItem -LiteralPath $wrapper -Recurse -File |
        Where-Object {
            $_.Extension -in ".pdb", ".cs", ".csproj", ".slnx" -or
            $_.Name -eq "appsettings.Development.json" -or
            $_.Name -like "*.tmp" -or
            $_.Name -like "*.partial"
        }
    if ($forbidden) {
        throw "package_forbidden_file:$($forbidden[0].FullName)"
    }

    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }
    Compress-Archive -LiteralPath $wrapper -DestinationPath $archive -CompressionLevel Optimal

    $verify = Join-Path $staging "verify"
    Microsoft.PowerShell.Archive\Expand-Archive `
        -LiteralPath $archive `
        -DestinationPath $verify
    if (-not (Test-Path -LiteralPath (Join-Path $verify "$wrapperName/CipherVault.Server.exe") -PathType Leaf)) {
        throw "package_layout_invalid"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $verify "$wrapperName/wwwroot/index.html") -PathType Leaf)) {
        throw "package_wwwroot_invalid"
    }
    Write-Output $archive
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
