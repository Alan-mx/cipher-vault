$ErrorActionPreference = 'Stop'

$v2Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
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
    dotnet restore CipherVault.V2.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'restore failed' }

    dotnet build CipherVault.V2.slnx --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }

    dotnet test CipherVault.V2.slnx --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
}
finally {
    Pop-Location
}
