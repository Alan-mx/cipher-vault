param(
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "$PSScriptRoot\..\artifacts\publish"
)

$ErrorActionPreference = "Stop"

$projects = @(
    @{ Name = "ui"; Project = "src\PersonalVault.Ui.Wpf\PersonalVault.Ui.Wpf.csproj" },
    @{ Name = "mcp"; Project = "src\PersonalVault.Mcp\PersonalVault.Mcp.csproj" },
    @{ Name = "cli"; Project = "src\PersonalVault.Cli\PersonalVault.Cli.csproj" }
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
