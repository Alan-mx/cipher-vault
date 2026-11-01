$ErrorActionPreference = 'Stop'

$v2Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path (Split-Path $v2Root -Parent) 'CipherVault.slnx'
$sourceRoot = Join-Path $v2Root 'src/CipherVault.Core'
$forbidden = @(
    'SQLCipher',
    'bundle_e_sqlcipher',
    'RecoveryKey',
    'RecoveryVerificationRequired',
    'recovery_slot',
    'ILogger',
    'Console.Write',
    'Serilog',
    'NLog'
)

$sourceFiles = Get-ChildItem -LiteralPath $sourceRoot -Recurse -File |
    Where-Object { $_.Extension -in '.cs', '.csproj' }
foreach ($file in $sourceFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($value in $forbidden) {
        if ($content.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Forbidden legacy crypto reference '$value' in $($file.FullName)"
        }
    }
}

Push-Location $v2Root
try {
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'restore failed' }

    dotnet build $solution --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }

    dotnet test $solution --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
}
finally {
    Pop-Location
}
