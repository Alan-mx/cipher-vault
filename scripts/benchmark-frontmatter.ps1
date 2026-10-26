param(
    [int]$Iterations = 10000
)

$ErrorActionPreference = 'Stop'
$env:CIPHER_VAULT_RUN_FRONTMATTER_PERF = '1'
$env:CIPHER_VAULT_FRONTMATTER_PERF_ITERATIONS = $Iterations.ToString()

try {
    dotnet test `
        "$PSScriptRoot\..\tests\CipherVault.Core.Tests\CipherVault.Core.Tests.csproj" `
        -c Release `
        --nologo `
        --filter "FullyQualifiedName~FrontMatterPerformanceTests" `
        --logger "console;verbosity=normal"
}
finally {
    Remove-Item Env:\CIPHER_VAULT_RUN_FRONTMATTER_PERF -ErrorAction SilentlyContinue
    Remove-Item Env:\CIPHER_VAULT_FRONTMATTER_PERF_ITERATIONS -ErrorAction SilentlyContinue
}
