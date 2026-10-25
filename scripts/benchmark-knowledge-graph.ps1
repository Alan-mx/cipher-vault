param(
    [int]$Documents = 1000,
    [int]$LinksPerDocument = 10
)

$ErrorActionPreference = 'Stop'
$env:CIPHER_VAULT_RUN_PERF = '1'
$env:CIPHER_VAULT_PERF_DOCS = $Documents.ToString()
$env:CIPHER_VAULT_PERF_LINKS = $LinksPerDocument.ToString()

try {
    dotnet test `
        "$PSScriptRoot\..\tests\CipherVault.Core.Tests\CipherVault.Core.Tests.csproj" `
        -c Release `
        --nologo `
        --filter "FullyQualifiedName~KnowledgeGraphPerformanceTests" `
        --logger "console;verbosity=normal"
}
finally {
    Remove-Item Env:\CIPHER_VAULT_RUN_PERF -ErrorAction SilentlyContinue
    Remove-Item Env:\CIPHER_VAULT_PERF_DOCS -ErrorAction SilentlyContinue
    Remove-Item Env:\CIPHER_VAULT_PERF_LINKS -ErrorAction SilentlyContinue
}
