$ErrorActionPreference = "Stop"

$v2Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$solution = Join-Path (Split-Path $v2Root -Parent) "CipherVault.slnx"
$frontendRoot = Join-Path $v2Root "frontend"
$serverProject = Join-Path $v2Root "src/CipherVault.Server/CipherVault.Server.csproj"
$indexPath = Join-Path $v2Root "src/CipherVault.Server/wwwroot/index.html"

function Assert-Success {
    param(
        [string] $Step,
        [int] $ExitCode
    )

    if ($ExitCode -ne 0) {
        throw "$Step failed with exit code $ExitCode."
    }
}

$forbiddenFrontend = @(
    "serviceWorker.register",
    "unsafe-eval",
    "cdn.jsdelivr.net",
    "unpkg.com",
    "cdnjs.cloudflare.com"
)
$frontendFiles = Get-ChildItem -LiteralPath (Join-Path $frontendRoot "src") `
    -Recurse -File |
    Where-Object { $_.Extension -in ".ts", ".vue", ".css" }
foreach ($file in $frontendFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($value in $forbiddenFrontend) {
        if ($content.IndexOf($value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Forbidden frontend runtime pattern '$value' in $($file.FullName)"
        }
    }
}

Push-Location $frontendRoot
try {
    npm ci --no-audit --no-fund
    Assert-Success "npm ci" $LASTEXITCODE
    npm run format:check
    Assert-Success "npm run format:check" $LASTEXITCODE
    npm run typecheck
    Assert-Success "npm run typecheck" $LASTEXITCODE
    npm run lint
    Assert-Success "npm run lint" $LASTEXITCODE
    npm test
    Assert-Success "npm test" $LASTEXITCODE
    npm run build
    Assert-Success "npm run build" $LASTEXITCODE
}
finally {
    Pop-Location
}

if (-not (Test-Path $indexPath)) {
    throw "Frontend build did not produce $indexPath."
}

Push-Location $v2Root
try {
    dotnet restore $solution --locked-mode -p:NuGetAudit=false
    Assert-Success "dotnet restore" $LASTEXITCODE
    dotnet format $solution --verify-no-changes --no-restore
    Assert-Success "dotnet format" $LASTEXITCODE
    dotnet build $solution -c Release --no-restore -warnaserror
    Assert-Success "dotnet build" $LASTEXITCODE

    $resultsRoot = Join-Path $v2Root "artifacts/test-results/windows"
    New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null
    $tests = @(
        @{
            Project = "tests/CipherVault.Core.Tests/CipherVault.Core.Tests.csproj"
            File = "phase6-windows-core.trx"
        },
        @{
            Project = "tests/CipherVault.Application.Tests/CipherVault.Application.Tests.csproj"
            File = "phase6-windows-application.trx"
        },
        @{
            Project = "tests/CipherVault.Server.Tests/CipherVault.Server.Tests.csproj"
            File = "phase6-windows-server.trx"
        }
    )
    foreach ($test in $tests) {
        dotnet test $test.Project `
            -c Release --no-build --no-restore `
            --logger "trx;LogFileName=$($test.File)" `
            --results-directory $resultsRoot
        Assert-Success "dotnet test $($test.Project)" $LASTEXITCODE
    }

    foreach ($test in $tests) {
        $trxPath = Join-Path $resultsRoot $test.File
        [xml] $result = Get-Content -LiteralPath $trxPath -Raw
        $counters = $result.TestRun.ResultSummary.Counters
        if (([int]$counters.failed -ne 0) -or ([int]$counters.notExecuted -ne 0) -or ([int]$counters.error -ne 0)) {
            throw "Phase 6 TRX contains failures/errors/skips: $trxPath"
        }
    }

    dotnet build $serverProject -c Release --no-restore -warnaserror
    Assert-Success "dotnet build Server" $LASTEXITCODE
}
finally {
    Pop-Location
}

Push-Location $frontendRoot
try {
    npm run test:e2e
    Assert-Success "npm run test:e2e" $LASTEXITCODE
}
finally {
    Pop-Location
}

Write-Output "Phase 6 Windows verification passed."
Write-Output "Frontend build, unit tests, Playwright, and .NET TRX evidence are current."
