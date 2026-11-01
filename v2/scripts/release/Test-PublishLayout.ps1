param(
    [Parameter(Mandatory)]
    [string] $PublishRoot,
    [Parameter(Mandatory)]
    [ValidateSet("win-x64", "linux-x64")]
    [string] $Rid
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $PublishRoot).Path
$server = if ($Rid -eq "win-x64") {
    Join-Path $root "CipherVault.Server.exe"
}
else {
    Join-Path $root "CipherVault.Server"
}
$required = @(
    $server,
    (Join-Path $root "CipherVault.Server.runtimeconfig.json"),
    (Join-Path $root "wwwroot/index.html")
)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "publish_layout_missing:$path"
    }
}

$nativeName = if ($Rid -eq "win-x64") {
    "sqlite3mc.dll"
}
else {
    "libsqlite3mc.so"
}
if (-not (Get-ChildItem -LiteralPath $root -Recurse -File |
        Where-Object { $_.Name -eq $nativeName } |
        Select-Object -First 1)) {
    throw "publish_native_missing:$nativeName"
}

$forbidden = Get-ChildItem -LiteralPath $root -Recurse -File |
    Where-Object {
        $_.Extension -in ".pdb", ".cs" -or
        $_.Name -eq "appsettings.Development.json" -or
        $_.Name -like "*.tmp" -or
        $_.Name -like "*.partial"
    }
if ($forbidden) {
    throw "publish_forbidden_file:$($forbidden[0].FullName)"
}

Write-Output "publish_layout_valid"
