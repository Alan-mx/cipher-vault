using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Search;

namespace CipherVault.Application.Tests;

public sealed class PropertyFilterSearchTests
{
    [Fact]
    public async Task PropertyFiltersWorkWithKeywordsAndExistingFilters()
    {
        await using var fixture = await CreateFixtureAsync();

        var keyword = await fixture.Search.SearchAsync(
            Query(
                "needle",
                Filter("source.kind", SearchPropertyOperator.Eq, "\"project\"")));
        Assert.Equal(["A"], keyword.Hits.Select(hit => hit.Title));

        var shortKeyword = await fixture.Search.SearchAsync(
            Query(
                "ne",
                Filter("source.kind", SearchPropertyOperator.Eq, "\"project\"")));
        Assert.Equal(["A"], shortKeyword.Hits.Select(hit => hit.Title));

        var existingFilter = await fixture.Search.SearchAsync(
            new SearchQuery(
                string.Empty,
                ["Redis"],
                null,
                null,
                null,
                null,
                false,
                new CursorPageRequest(),
                [Filter("status", SearchPropertyOperator.Eq, "\"raw\"")],
                SearchPropertyMatch.All));
        Assert.Equal(["B"], existingFilter.Hits.Select(hit => hit.Title));
    }

    [Fact]
    public async Task AllAnyExistsAndStrictTypesBehaveAsSpecified()
    {
        await using var fixture = await CreateFixtureAsync();

        var all = await fixture.Search.SearchAsync(
            Query(
                "",
                Filter("status", SearchPropertyOperator.Eq, "\"verified\""),
                Filter("maturity", SearchPropertyOperator.Eq, "\"practiced\"")));
        Assert.Equal(["A"], all.Hits.Select(hit => hit.Title));

        var any = await fixture.Search.SearchAsync(
            Query(
                "",
                SearchPropertyMatch.Any,
                Filter("status", SearchPropertyOperator.Eq, "\"raw\""),
                Filter("source.kind", SearchPropertyOperator.Eq, "\"project\"")));
        Assert.Equal(["A", "B"], any.Hits.Select(hit => hit.Title).Order());

        var missing = await fixture.Search.SearchAsync(
            Query(
                "",
                Filter("nullable", SearchPropertyOperator.Exists, "false")));
        Assert.Equal(["A", "B"], missing.Hits.Select(hit => hit.Title).Order());

        var explicitNull = await fixture.Search.SearchAsync(
            Query(
                "",
                Filter("nullable", SearchPropertyOperator.Eq, "null")));
        Assert.Equal(["C"], explicitNull.Hits.Select(hit => hit.Title));

        var number = await fixture.Search.SearchAsync(
            Query(
                "",
                Filter("source.count", SearchPropertyOperator.Eq, "3")));
        Assert.Equal(["A"], number.Hits.Select(hit => hit.Title));

        var stringDoesNotEqualNumber = await fixture.Search.SearchAsync(
            Query(
                "",
                Filter("source.count", SearchPropertyOperator.Eq, "\"3\"")));
        Assert.Empty(stringDoesNotEqualNumber.Hits);

        var boolean = await fixture.Search.SearchAsync(
            Query(
                "",
                Filter("active", SearchPropertyOperator.Eq, "true")));
        Assert.Equal(["A"], boolean.Hits.Select(hit => hit.Title));

        var stringDoesNotEqualBoolean = await fixture.Search.SearchAsync(
            Query(
                "",
                Filter("active", SearchPropertyOperator.Eq, "\"true\"")));
        Assert.Empty(stringDoesNotEqualBoolean.Hits);
    }

    [Fact]
    public async Task ContainsInAndRangesUseTypedSemantics()
    {
        await using var fixture = await CreateFixtureAsync();

        var arrayContains = await fixture.Search.SearchAsync(
            Query(
                "",
                Filter("topics", SearchPropertyOperator.Contains, "\"Redis\"")));
        Assert.Equal(["A"], arrayContains.Hits.Select(hit => hit.Title));

        var arrayContainsWrongCase = await fixture.Search.SearchAsync(
            Query(
                "",
                Filter("topics", SearchPropertyOperator.Contains, "\"redis\"")));
        Assert.Empty(arrayContainsWrongCase.Hits);

        var stringContains = await fixture.Search.SearchAsync(
            Query(
                "",
                Filter(
                    "description",
                    SearchPropertyOperator.Contains,
                    "\"CACHÉ CACHING\"")));
        Assert.Equal(["A"], stringContains.Hits.Select(hit => hit.Title));

        var booleanArrayContains = await fixture.Search.SearchAsync(
            Query(
                "",
                Filter(
                    "flags",
                    SearchPropertyOperator.Contains,
                    "false")));
        Assert.Equal(["A"], booleanArrayContains.Hits.Select(hit => hit.Title));

        var membership = await fixture.Search.SearchAsync(
            Query(
                "",
                Filter(
                    "status",
                    SearchPropertyOperator.In,
                    "[\"archived\", \"verified\", \"verified\"]")));
        Assert.Equal(["A"], membership.Hits.Select(hit => hit.Title));

        var range = await fixture.Search.SearchAsync(
            Query(
                "",
                Filter("source.count", SearchPropertyOperator.Gte, "2"),
                Filter("source.count", SearchPropertyOperator.Lte, "4")));
        Assert.Equal(["A"], range.Hits.Select(hit => hit.Title));

        var stringRange = await fixture.Search.SearchAsync(
            Query(
                "",
                Filter("description", SearchPropertyOperator.Gte, "\"CACH\""),
                Filter("description", SearchPropertyOperator.Lte, "\"CAFZ\"")));
        Assert.Equal(["A"], stringRange.Hits.Select(hit => hit.Title));
    }

    [Fact]
    public async Task CursorIsBoundToPropertyFilters()
    {
        await using var fixture = await CreateFixtureAsync();
        var first = await fixture.Search.SearchAsync(
            new SearchQuery(
                string.Empty,
                null,
                null,
                null,
                null,
                null,
                false,
                new CursorPageRequest(1),
                [Filter("nullable", SearchPropertyOperator.Exists, "false")],
                SearchPropertyMatch.All));
        Assert.Single(first.Hits);
        Assert.NotNull(first.NextCursor);

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Search.SearchAsync(
                new SearchQuery(
                    string.Empty,
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    new CursorPageRequest(1, first.NextCursor),
                    [Filter("status", SearchPropertyOperator.Eq, "\"verified\"")],
                    SearchPropertyMatch.All)));
        Assert.Equal(VaultErrorCode.InvalidCursor, exception.Code);
    }

    [Fact]
    public async Task InvalidFiltersReturnInvalidInput()
    {
        await using var fixture = await CreateFixtureAsync();

        await AssertInvalidAsync(
            fixture,
            Query("", Filter("tags", SearchPropertyOperator.Exists, "true")));
        await AssertInvalidAsync(
            fixture,
            Query("", Filter("source.kind", SearchPropertyOperator.Eq, "{}")));
        await AssertInvalidAsync(
            fixture,
            Query("", Filter("status", SearchPropertyOperator.In, "[]")));
        await AssertInvalidAsync(
            fixture,
            Query("", Filter("status", SearchPropertyOperator.Gte, "true")));
        await AssertInvalidAsync(
            fixture,
            new SearchQuery(
                " ",
                [],
                null,
                null,
                null,
                null,
                false,
                new CursorPageRequest()));
    }

    private static async Task AssertInvalidAsync(
        VaultApplicationFixture fixture,
        SearchQuery query)
    {
        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Search.SearchAsync(query));
        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    private static async Task<VaultApplicationFixture> CreateFixtureAsync()
    {
        var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        _ = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "A",
                "notes/a.md",
                null,
                VaultDocumentFormat.Markdown,
                """
                ---
                status: verified
                maturity: practiced
                source:
                  kind: project
                  count: 3
                topics: [Redis, Cache]
                flags: [true, false]
                active: true
                description: CACHÉ Caching Strategy
                tags: [Redis]
                ---

                needle A
                """,
                null),
            actor);
        _ = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "B",
                "notes/b.md",
                null,
                VaultDocumentFormat.Markdown,
                """
                ---
                status: raw
                maturity: raw
                source:
                  kind: conversation
                tags: [Redis]
                ---

                needle B
                """,
                null),
            actor);
        _ = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "C",
                "notes/c.md",
                null,
                VaultDocumentFormat.Markdown,
                """
                ---
                nullable: null
                ---

                needle C
                """,
                null),
            actor);
        return fixture;
    }

    private static SearchQuery Query(
        string query,
        params SearchPropertyFilter[] filters) =>
        new(
            query,
            null,
            null,
            null,
            null,
            null,
            false,
            new CursorPageRequest(),
            filters,
            SearchPropertyMatch.All);

    private static SearchQuery Query(
        string query,
        SearchPropertyMatch match,
        params SearchPropertyFilter[] filters) =>
        new(
            query,
            null,
            null,
            null,
            null,
            null,
            false,
            new CursorPageRequest(),
            filters,
            match);

    private static SearchPropertyFilter Filter(
        string field,
        SearchPropertyOperator operation,
        string valueJson)
    {
        using var document = JsonDocument.Parse(valueJson);
        return new SearchPropertyFilter(
            field,
            operation,
            document.RootElement.Clone());
    }
}
