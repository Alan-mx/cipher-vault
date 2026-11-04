using CipherVault.Core.Database;
using CipherVault.Core.Search;

namespace CipherVault.Core.Tests;

public sealed class SearchResultGuardTests
{
    [Fact]
    public void SearchPageWithExtendedHitContractsCrossesDatabaseBoundary()
    {
        var hit = new SearchHit(
            Guid.NewGuid(),
            "Title",
            "notes/title.md",
            1,
            "snippet",
            1.2)
        {
            UpdatedAt = DateTimeOffset.UtcNow
        };
        VaultOperationResultGuard.ValidateResult(hit);
        var coverage = new SearchCoverage(1, 1, 1, 1, false);
        VaultOperationResultGuard.ValidateResult(coverage);
        VaultOperationResultGuard.ValidateResult(
            new SearchPage([], coverage, null));
        var page = new SearchPage(new SearchHit[] { hit }, coverage, null);

        VaultOperationResultGuard.ValidateResult(page);
    }
}
