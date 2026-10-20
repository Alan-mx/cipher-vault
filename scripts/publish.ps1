param(
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "$PSScriptRoot\..\artifacts\publish"
)

$ErrorActionPreference = "Stop"

$projects = @(
    @{ Name = "ui"; Project = "src\CipherVault.Ui.Wpf\CipherVault.Ui.Wpf.csproj" },
    @{ Name = "mcp"; Project = "src\CipherVault.Mcp\CipherVault.Mcp.csproj" },
    @{ Name = "cli"; Project = "src\CipherVault.Cli\CipherVault.Cli.csproj" }
)

foreach ($item in $projects) {
    $output = Join-Path $OutputRoot $item.Name
    dotnet publish $item.Project `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false `
        -o $output
    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed for $($item.Name)"
    }
}

Write-Host "Published to $OutputRoot"
