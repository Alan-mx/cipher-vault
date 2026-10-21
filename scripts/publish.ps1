param(
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "$PSScriptRoot\..\artifacts\publish"
)

$ErrorActionPreference = "Stop"

$projects = @(
    @{ Name = "ui"; Project = "src\CipherVault.Ui.Wpf\CipherVault.Ui.Wpf.csproj" },
    @{ Name = "mcp"; Project = "src\CipherVault.Mcp\CipherVault.Mcp.csproj" },
    @{ Name = "cli"; Project = "src\CipherVault.Cli\CipherVault.Cli.csproj" },
    @{ Name = "host"; Project = "src\CipherVault.Host\CipherVault.Host.csproj" }
)

$stage = Join-Path $OutputRoot "CipherVault-win-x64"
if (Test-Path $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null

foreach ($item in $projects) {
    $output = Join-Path $OutputRoot ".publish-$($item.Name)"
    if (Test-Path $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    dotnet publish $item.Project `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -o $output
    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed for $($item.Name)"
    }

    $executables = Get-ChildItem -LiteralPath $output -Filter "*.exe" -File
    if ($executables.Count -eq 0) {
        throw "No executable was produced for $($item.Name)"
    }
    foreach ($executable in $executables) {
        Copy-Item -LiteralPath $executable.FullName -Destination $stage -Force
    }
    Remove-Item -LiteralPath $output -Recurse -Force
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot "..\README.md") -Destination $stage
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "..\LICENSE") -Destination $stage

$archive = Join-Path $OutputRoot "CipherVault-win-x64.zip"
if (Test-Path $archive) {
    Remove-Item -LiteralPath $archive -Force
}
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $archive

Write-Host "Published ZIP: $archive"
