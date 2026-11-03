param(
    [Parameter(Mandatory)]
    [string] $PublishRoot,
    [Parameter(Mandatory)]
    [ValidateSet("win-x64", "linux-x64", "linux-arm64")]
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
$nativeFiles = @(
    Get-ChildItem -LiteralPath $root -Recurse -File |
        Where-Object { $_.Name -eq $nativeName }
)
if ($nativeFiles.Count -eq 0) {
    throw "publish_native_missing:$nativeName"
}

function Get-ElfHeader {
    param([string] $Path)

    $stream = [IO.File]::OpenRead($Path)
    try {
        $bytes = New-Object byte[] 20
        $read = $stream.Read($bytes, 0, $bytes.Length)
    }
    finally {
        $stream.Dispose()
    }
    if ($read -lt 20 -or
        $bytes[0] -ne 0x7F -or
        $bytes[1] -ne 0x45 -or
        $bytes[2] -ne 0x4C -or
        $bytes[3] -ne 0x46) {
        throw "publish_elf_magic_invalid:$Path"
    }
    if ($bytes[4] -ne 2) {
        throw "publish_elf_class_invalid:$Path"
    }
    if ($bytes[5] -ne 1) {
        throw "publish_elf_endian_invalid:$Path"
    }
    [pscustomobject]@{
        Machine = [BitConverter]::ToUInt16($bytes, 18)
    }
}

function Assert-ElfArchitecture {
    param(
        [string] $Path,
        [string] $Expected
    )

    $header = Get-ElfHeader $Path
    $actual = switch ($header.Machine) {
        0x003E { "x86-64" }
        0x00B7 { "AArch64" }
        default { "0x{0:X4}" -f $header.Machine }
    }
    if ($actual -ne $Expected) {
        throw "publish_elf_machine_invalid:${Path}:${actual}"
    }
}

if ($Rid -in "linux-x64", "linux-arm64") {
    $expectedMachine = if ($Rid -eq "linux-arm64") {
        "AArch64"
    }
    else {
        "x86-64"
    }
    Assert-ElfArchitecture -Path $server -Expected $expectedMachine
    foreach ($nativeFile in $nativeFiles) {
        Assert-ElfArchitecture `
            -Path $nativeFile.FullName `
            -Expected $expectedMachine
    }
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
        $stream = [IO.File]::OpenRead($file.FullName)
        try {
            $magic = New-Object byte[] 4
            $read = $stream.Read($magic, 0, $magic.Length)
        }
        finally {
            $stream.Dispose()
        }
        if ($read -eq 4 -and
            $magic[0] -eq 0x7F -and
            $magic[1] -eq 0x45 -and
            $magic[2] -eq 0x4C -and
            $magic[3] -eq 0x46) {
            Assert-ElfArchitecture `
                -Path $file.FullName `
                -Expected $expectedMachine
        }
    }
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
