$ErrorActionPreference = 'Stop'

$v2Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$applicationRoot = Join-Path $v2Root 'src/CipherVault.Application'
$applicationProject = Join-Path $applicationRoot 'CipherVault.Application.csproj'
$phase2CoreFiles = @(
    (Join-Path $v2Root 'src/CipherVault.Core/Database/VaultDatabaseRuntime.cs'),
    (Join-Path $v2Root 'src/CipherVault.Core/Database/VaultOperationContext.cs'),
    (Join-Path $v2Root 'src/CipherVault.Core/Database/VaultOperationResultGuard.cs'),
    (Join-Path $v2Root 'src/CipherVault.Core/Database/VaultCommandLease.cs')
)

$legacyTokens = @(
    'SQLCipher',
    'bundle_e_sqlcipher',
    'RecoveryKey',
    'RecoveryVerificationRequired',
    'recovery_slot',
    'Agent Grant',
    'Approval Token',
    'Broker',
    'Named Pipe',
    'HardLifetime',
    'Console.Write',
    'Serilog',
    'NLog'
)
$applicationForbidden = @(
    'Microsoft.Data.Sqlite',
    'SQLitePCL',
    'Microsoft.AspNetCore',
    'ModelContextProtocol',
    'SqliteConnection',
    'SqliteCommand',
    'SqliteDataReader',
    'SqliteTransaction'
)

$applicationFiles = Get-ChildItem -LiteralPath $applicationRoot -Recurse -File |
    Where-Object { $_.Extension -eq '.cs' }
foreach ($file in $applicationFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($value in ($legacyTokens + $applicationForbidden)) {
        if ($content.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Forbidden Application reference '$value' in $($file.FullName)"
        }
    }
}

$projectContent = Get-Content -LiteralPath $applicationProject -Raw
foreach ($value in $applicationForbidden) {
    if ($projectContent.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Forbidden Application project reference '$value'."
    }
}

foreach ($file in $phase2CoreFiles) {
    $content = Get-Content -LiteralPath $file -Raw
    foreach ($value in $legacyTokens) {
        if ($content.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Forbidden legacy reference '$value' in $file"
        }
    }

    $usesNoOpCancel =
        $content.IndexOf('SqliteCommand.Cancel', [StringComparison]::OrdinalIgnoreCase) -ge 0
    $usesNoOpCancel = $usesNoOpCancel -or
        ($content.IndexOf('.Command.Cancel', [StringComparison]::OrdinalIgnoreCase) -ge 0)
    if ($usesNoOpCancel) {
        throw "SqliteCommand.Cancel is a no-op and cannot implement Vault interrupt: $file"
    }
}

$runtimeContent = Get-Content -LiteralPath $phase2CoreFiles[0] -Raw
if ($runtimeContent.IndexOf(
        'raw.sqlite3_interrupt(session.Connection.Handle)',
        [StringComparison]::Ordinal) -lt 0) {
    throw 'Vault interrupt must use SQLitePCL raw.sqlite3_interrupt with the public SqliteConnection.Handle.'
}

Push-Location $v2Root
try {
    dotnet restore CipherVault.V2.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'restore failed' }

    dotnet format CipherVault.V2.slnx --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'format verification failed' }

    dotnet build CipherVault.V2.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }

    $resultsRoot = Join-Path $v2Root 'artifacts/test-results/windows'
    New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null
    dotnet test tests/CipherVault.Core.Tests/CipherVault.Core.Tests.csproj `
        -c Release --no-build --no-restore `
        --logger "trx;LogFileName=phase2-windows-core.trx" `
        --results-directory $resultsRoot
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }

    dotnet test tests/CipherVault.Application.Tests/CipherVault.Application.Tests.csproj `
        -c Release --no-build --no-restore `
        --logger "trx;LogFileName=phase2-windows-application.trx" `
        --results-directory $resultsRoot
    if ($LASTEXITCODE -ne 0) { throw 'Application tests failed' }
}
finally {
    Pop-Location
}
