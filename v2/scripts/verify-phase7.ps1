$ErrorActionPreference = "Stop"

$v2Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$frontendRoot = Join-Path $v2Root "frontend"
$probeProject = Join-Path $v2Root "tests/CipherVault.Phase7.ProcessProbe/CipherVault.Phase7.ProcessProbe.csproj"
$password = "phase7 cross platform password"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$crossRoot = Join-Path $v2Root "artifacts/cross-platform/phase7"
$windowsArtifactRoot = Join-Path $crossRoot "windows"
$linuxArtifactRoot = Join-Path $crossRoot "linux"
$windowsPackage = Join-Path $windowsArtifactRoot "latest-windows.cvbackup"
$linuxPackage = Join-Path $linuxArtifactRoot "latest-linux.cvbackup"

function Assert-Success {
    param([string] $Step, [int] $ExitCode)
    if ($ExitCode -ne 0) {
        throw "$Step failed with exit code $ExitCode."
    }
}

Push-Location $frontendRoot
try {
    npm ci --no-audit --no-fund
    Assert-Success "npm ci" $LASTEXITCODE
    npm run format:check
    Assert-Success "format" $LASTEXITCODE
    npm run typecheck
    Assert-Success "typecheck" $LASTEXITCODE
    npm run lint
    Assert-Success "lint" $LASTEXITCODE
    npm test
    Assert-Success "frontend tests" $LASTEXITCODE
    npm run build
    Assert-Success "frontend build" $LASTEXITCODE
}
finally {
    Pop-Location
}

Push-Location $v2Root
try {
    dotnet restore CipherVault.V2.slnx --locked-mode -p:NuGetAudit=false
    Assert-Success "restore" $LASTEXITCODE
    dotnet format CipherVault.V2.slnx --verify-no-changes --no-restore
    Assert-Success "format" $LASTEXITCODE
    dotnet build CipherVault.V2.slnx -c Release --no-restore -warnaserror
    Assert-Success "build" $LASTEXITCODE

    $resultsRoot = Join-Path $v2Root "artifacts/test-results/windows"
    New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null
    $tests = @(
        @{ Project = "tests/CipherVault.Core.Tests/CipherVault.Core.Tests.csproj"; File = "phase7-windows-core.trx" },
        @{ Project = "tests/CipherVault.Application.Tests/CipherVault.Application.Tests.csproj"; File = "phase7-windows-application.trx" },
        @{ Project = "tests/CipherVault.Server.Tests/CipherVault.Server.Tests.csproj"; File = "phase7-windows-server.trx" }
    )
    foreach ($test in $tests) {
        dotnet test $test.Project -c Release --no-build --no-restore `
            --logger "trx;LogFileName=$($test.File)" `
            --results-directory $resultsRoot
        Assert-Success "test $($test.Project)" $LASTEXITCODE
    }

    New-Item -ItemType Directory -Path $windowsArtifactRoot -Force | Out-Null
    $sourceVault = Join-Path $crossRoot "windows/run-$stamp/source"
    $sourceBackups = Join-Path $crossRoot "windows/run-$stamp/backups"
    $restored = Join-Path $crossRoot "windows/run-$stamp/restored"
    dotnet run --project $probeProject -c Release -- `
        create $sourceVault $sourceBackups $windowsPackage $password
    Assert-Success "Windows backup create" $LASTEXITCODE
    dotnet run --project $probeProject -c Release -- `
        restore $windowsPackage $restored $password
    Assert-Success "Windows-to-Windows restore" $LASTEXITCODE
    dotnet run --project $probeProject -c Release -- `
        verify $restored $password
    Assert-Success "Windows-to-Windows verify" $LASTEXITCODE

    if (Test-Path $linuxPackage) {
        $linuxRestored = Join-Path $crossRoot "windows/run-$stamp/restored-from-linux"
        dotnet run --project $probeProject -c Release -- `
            restore $linuxPackage $linuxRestored $password
        Assert-Success "Linux-to-Windows restore" $LASTEXITCODE
        dotnet run --project $probeProject -c Release -- `
            verify $linuxRestored $password
        Assert-Success "Linux-to-Windows verify" $LASTEXITCODE
    }
}
finally {
    Pop-Location
}

Push-Location $frontendRoot
try {
    npm run test:e2e
    Assert-Success "Playwright" $LASTEXITCODE
}
finally {
    Pop-Location
}

Write-Output "Phase 7 Windows verification passed."
Write-Output "Windows restore artifact: $windowsPackage"
