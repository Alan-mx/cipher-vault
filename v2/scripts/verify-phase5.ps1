$ErrorActionPreference = 'Stop'

$v2Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$serverRoot = Join-Path $v2Root 'src/CipherVault.Server'
$applicationRoot = Join-Path $v2Root 'src/CipherVault.Application'
$coreRoot = Join-Path $v2Root 'src/CipherVault.Core'
$serverProject = Join-Path $serverRoot 'CipherVault.Server.csproj'
$applicationProject = Join-Path $applicationRoot 'CipherVault.Application.csproj'
$coreProject = Join-Path $coreRoot 'CipherVault.Core.csproj'

$legacyTokens = @(
    'Grant',
    'Approval',
    'Broker',
    'Named Pipe',
    'Recovery',
    'IntegrityKey',
    'HMAC',
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
    'CipherVault.Core.Storage'
)

foreach ($root in @($applicationRoot, $coreRoot)) {
    $files = Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.cs'
    foreach ($file in $files) {
        $content = Get-Content -LiteralPath $file.FullName -Raw
        $forbidden = if ($root -eq $applicationRoot) {
            $legacyTokens + $applicationForbidden
        } else {
            $legacyTokens + $coreForbidden
        }
        foreach ($value in $forbidden) {
            if ($content.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "Forbidden Core/Application reference '$value' in $($file.FullName)"
            }
        }
    }
}

foreach ($project in @($applicationProject, $coreProject)) {
    $content = Get-Content -LiteralPath $project -Raw
    $forbidden = if ($project -eq $applicationProject) {
        $applicationForbidden
    } else {
        $coreForbidden
    }
    foreach ($value in $forbidden) {
        if ($content.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Forbidden project reference '$value' in $project"
        }
    }
}

$serverFiles = Get-ChildItem -LiteralPath $serverRoot -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
$fixedTimeCompare = $false
$toolCount = 0
foreach ($file in $serverFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($value in ($legacyTokens + $serverForbidden)) {
        if ($content.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Forbidden Server reference '$value' in $($file.FullName)"
        }
    }
    if ($content.IndexOf(
            'CryptographicOperations.FixedTimeEquals',
            [StringComparison]::Ordinal) -ge 0) {
        $fixedTimeCompare = $true
    }
    $toolCount += (
        [regex]::Matches(
            $content,
            '\[McpServerTool\(Name\s*=\s*McpToolNames\.[A-Za-z]+\)\]'
        )).Count
}
if (-not $fixedTimeCompare) {
    throw 'MCP token comparison must use CryptographicOperations.FixedTimeEquals.'
}
if ($toolCount -ne 15) {
    throw "Expected 15 MCP tools but found $toolCount."
}
if (-not (Test-Path (Join-Path $v2Root 'tests/CipherVault.Server.Tests/Containers/Dockerfile.phase5'))) {
    throw 'Phase 5 Dockerfile is missing.'
}

Push-Location $v2Root
try {
    dotnet restore CipherVault.V2.slnx --locked-mode -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'restore failed' }

    dotnet format CipherVault.V2.slnx --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'format verification failed' }

    dotnet build CipherVault.V2.slnx -c Release --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }

    $resultsRoot = Join-Path $v2Root 'artifacts/test-results/windows'
    New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null
    $trxFiles = @(
        (Join-Path $resultsRoot 'phase5-windows-core.trx'),
        (Join-Path $resultsRoot 'phase5-windows-application.trx'),
        (Join-Path $resultsRoot 'phase5-windows-server.trx')
    )

    dotnet test tests/CipherVault.Core.Tests/CipherVault.Core.Tests.csproj `
        -c Release --no-build --no-restore `
        --logger "trx;LogFileName=phase5-windows-core.trx" `
        --results-directory $resultsRoot
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }

    dotnet test tests/CipherVault.Application.Tests/CipherVault.Application.Tests.csproj `
        -c Release --no-build --no-restore `
        --logger "trx;LogFileName=phase5-windows-application.trx" `
        --results-directory $resultsRoot
    if ($LASTEXITCODE -ne 0) { throw 'Application tests failed' }

    dotnet test tests/CipherVault.Server.Tests/CipherVault.Server.Tests.csproj `
        -c Release --no-build --no-restore `
        --logger "trx;LogFileName=phase5-windows-server.trx" `
        --results-directory $resultsRoot
    if ($LASTEXITCODE -ne 0) { throw 'Server tests failed' }

    foreach ($trx in $trxFiles) {
        [xml]$result = Get-Content -LiteralPath $trx -Raw
        $counters = $result.TestRun.ResultSummary.Counters
        if (([int]$counters.failed -ne 0) -or
            ([int]$counters.notExecuted -ne 0) -or
            ([int]$counters.error -ne 0)) {
            throw "Phase 5 TRX contains failures, errors or skipped tests: $trx"
        }
    }

    Write-Output 'Phase 5 Windows verification passed.'
    $trxFiles | ForEach-Object { Write-Output "TRX: $_" }
}
finally {
    Pop-Location
}
