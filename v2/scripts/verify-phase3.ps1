$ErrorActionPreference = 'Stop'

$v2Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path (Split-Path $v2Root -Parent) 'CipherVault.slnx'
$applicationRoot = Join-Path $v2Root 'src/CipherVault.Application'
$applicationProject = Join-Path $applicationRoot 'CipherVault.Application.csproj'
$coreRoot = Join-Path $v2Root 'src/CipherVault.Core'
$domainRoots = @(
    $applicationRoot,
    (Join-Path $coreRoot 'Audit'),
    (Join-Path $coreRoot 'Documents'),
    (Join-Path $coreRoot 'Import'),
    (Join-Path $coreRoot 'KnowledgeGraph'),
    (Join-Path $coreRoot 'Search'),
    (Join-Path $coreRoot 'Storage')
)

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
    'ILogger',
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
    foreach ($value in ($legacyTokens + $loggingTokens + $applicationForbidden)) {
        if ($content.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Forbidden Phase 3 Application reference '$value' in $($file.FullName)"
        }
    }
}

$projectContent = Get-Content -LiteralPath $applicationProject -Raw
foreach ($value in $applicationForbidden) {
    if ($projectContent.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Forbidden Application project reference '$value'."
    }
}

$domainFiles = Get-ChildItem -LiteralPath $domainRoots -Recurse -File |
    Where-Object { $_.Extension -eq '.cs' }
foreach ($file in $domainFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($value in ($legacyTokens + $loggingTokens)) {
        if ($content.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Forbidden Phase 3 domain reference '$value' in $($file.FullName)"
        }
    }
}

Push-Location $v2Root
try {
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'restore failed' }

    dotnet format $solution --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'format verification failed' }

    dotnet build $solution -c Release --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }

    $resultsRoot = Join-Path $v2Root 'artifacts/test-results/windows'
    New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null
    $coreTrx = Join-Path $resultsRoot 'phase3-windows-core.trx'
    $applicationTrx = Join-Path $resultsRoot 'phase3-windows-application.trx'

    dotnet test tests/CipherVault.Core.Tests/CipherVault.Core.Tests.csproj `
        -c Release --no-build --no-restore `
        --logger "trx;LogFileName=phase3-windows-core.trx" `
        --results-directory $resultsRoot
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }

    dotnet test tests/CipherVault.Application.Tests/CipherVault.Application.Tests.csproj `
        -c Release --no-build --no-restore `
        --logger "trx;LogFileName=phase3-windows-application.trx" `
        --results-directory $resultsRoot
    if ($LASTEXITCODE -ne 0) { throw 'Application tests failed' }

    foreach ($trx in @($coreTrx, $applicationTrx)) {
        [xml]$result = Get-Content -LiteralPath $trx -Raw
        $counters = $result.TestRun.ResultSummary.Counters
        if (([int]$counters.failed -ne 0) -or
            ([int]$counters.notExecuted -ne 0) -or
            ([int]$counters.error -ne 0)) {
            throw "Phase 3 TRX contains failures, errors or skipped tests: $trx"
        }
    }

    Write-Output "Phase 3 Windows verification passed."
    Write-Output "TRX: $coreTrx"
    Write-Output "TRX: $applicationTrx"
}
finally {
    Pop-Location
}
