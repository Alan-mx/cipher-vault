$ErrorActionPreference = 'Stop'

$v2Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path (Split-Path $v2Root -Parent) 'CipherVault.slnx'
$applicationRoot = Join-Path $v2Root 'src/CipherVault.Application'
$coreRoot = Join-Path $v2Root 'src/CipherVault.Core'
$serverRoot = Join-Path $v2Root 'src/CipherVault.Server'
$serverEndpoints = Join-Path $serverRoot 'Api/Endpoints'
$applicationProject = Join-Path $applicationRoot 'CipherVault.Application.csproj'
$coreProject = Join-Path $coreRoot 'CipherVault.Core.csproj'
$serverProject = Join-Path $serverRoot 'CipherVault.Server.csproj'

$legacyTokens = @(
    'Recovery',
    'IntegrityKey',
    'HMAC',
    'Access Grant',
    'Approval',
    'Broker',
    'Named Pipe',
    'HardLifetime'
)
$loggingTokens = @(
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
$coreForbidden = @(
    'Microsoft.AspNetCore',
    'ModelContextProtocol'
)
$serverForbidden = @(
    'Microsoft.Data.Sqlite',
    'SQLitePCL',
    'CipherVault.Core.Storage',
    'ModelContextProtocol',
    'MapMcp',
    'BackupService',
    '/api/backups'
)

$applicationFiles = Get-ChildItem -LiteralPath $applicationRoot -Recurse -File -Filter '*.cs'
foreach ($file in $applicationFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($value in ($legacyTokens + $loggingTokens + $applicationForbidden)) {
        if ($content.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Forbidden Application reference '$value' in $($file.FullName)"
        }
    }
}

$coreFiles = Get-ChildItem -LiteralPath $coreRoot -Recurse -File -Filter '*.cs'
foreach ($file in $coreFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($value in ($legacyTokens + $loggingTokens + $coreForbidden)) {
        if ($content.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Forbidden Core reference '$value' in $($file.FullName)"
        }
    }
}

$applicationProjectContent = Get-Content -LiteralPath $applicationProject -Raw
foreach ($value in $applicationForbidden) {
    if ($applicationProjectContent.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Forbidden Application project reference '$value'."
    }
}

$coreProjectContent = Get-Content -LiteralPath $coreProject -Raw
foreach ($value in $coreForbidden) {
    if ($coreProjectContent.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Forbidden Core project reference '$value'."
    }
}

$serverFiles = Get-ChildItem -LiteralPath $serverRoot -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
foreach ($file in $serverFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($value in ($legacyTokens + $serverForbidden)) {
        if ($content.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Forbidden Server reference '$value' in $($file.FullName)"
        }
    }
}

$routeCount = (
    Get-ChildItem -LiteralPath $serverEndpoints -File -Filter '*.cs' |
        Select-String -Pattern '\.Map(Get|Post|Put|Patch|Delete)\(' |
        Measure-Object
).Count
if ($routeCount -ne 28) {
    throw "Expected 28 Phase 4 routes but found $routeCount."
}

Push-Location $v2Root
try {
    dotnet restore $solution --locked-mode -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'restore failed' }

    dotnet format $solution --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'format verification failed' }

    dotnet build $solution -c Release --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }

    $resultsRoot = Join-Path $v2Root 'artifacts/test-results/windows'
    New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null
    $trxFiles = @(
        (Join-Path $resultsRoot 'phase4-windows-core.trx'),
        (Join-Path $resultsRoot 'phase4-windows-application.trx'),
        (Join-Path $resultsRoot 'phase4-windows-server.trx')
    )

    dotnet test tests/CipherVault.Core.Tests/CipherVault.Core.Tests.csproj `
        -c Release --no-build --no-restore `
        --logger "trx;LogFileName=phase4-windows-core.trx" `
        --results-directory $resultsRoot
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }

    dotnet test tests/CipherVault.Application.Tests/CipherVault.Application.Tests.csproj `
        -c Release --no-build --no-restore `
        --logger "trx;LogFileName=phase4-windows-application.trx" `
        --results-directory $resultsRoot
    if ($LASTEXITCODE -ne 0) { throw 'Application tests failed' }

    dotnet test tests/CipherVault.Server.Tests/CipherVault.Server.Tests.csproj `
        -c Release --no-build --no-restore `
        --logger "trx;LogFileName=phase4-windows-server.trx" `
        --results-directory $resultsRoot
    if ($LASTEXITCODE -ne 0) { throw 'Server tests failed' }

    foreach ($trx in $trxFiles) {
        [xml]$result = Get-Content -LiteralPath $trx -Raw
        $counters = $result.TestRun.ResultSummary.Counters
        if (([int]$counters.failed -ne 0) -or
            ([int]$counters.notExecuted -ne 0) -or
            ([int]$counters.error -ne 0)) {
            throw "Phase 4 TRX contains failures, errors or skipped tests: $trx"
        }
    }

    Write-Output 'Phase 4 Windows verification passed.'
    $trxFiles | ForEach-Object { Write-Output "TRX: $_" }
}
finally {
    Pop-Location
}
