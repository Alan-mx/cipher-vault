param(
    [int]$Documents = 10005
)

$ErrorActionPreference = 'Stop'
$env:CIPHER_VAULT_RUN_SEARCH_PERF = '1'
$env:CIPHER_VAULT_SEARCH_PERF_DOCS = $Documents.ToString()

try {
    dotnet test `
        "$PSScriptRoot\..\tests\CipherVault.Core.Tests\CipherVault.Core.Tests.csproj" `
        -c Release `
        --nologo `
        --filter "FullyQualifiedName~SearchPerformanceTests" `
        --logger "console;verbosity=normal"
}
finally {
    Remove-Item Env:\CIPHER_VAULT_RUN_SEARCH_PERF -ErrorAction SilentlyContinue
    Remove-Item Env:\CIPHER_VAULT_SEARCH_PERF_DOCS -ErrorAction SilentlyContinue
}
