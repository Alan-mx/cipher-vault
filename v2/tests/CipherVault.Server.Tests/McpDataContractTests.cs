using System.Text.Json;
using CipherVault.Application.Documents;
using CipherVault.Core.Documents;
using CipherVault.Server.Api;
using CipherVault.Server.Mcp;

namespace CipherVault.Server.Tests;

public sealed class McpDataContractTests
{
    [Fact]
    public void PartialUpdateParserPreservesOmittedAndEmptyValues()
    {
        using var json = JsonDocument.Parse(
            """
            {
              "title": "Renamed",
              "tags": [],
              "content": ""
            }
            """);

        var parsed = McpPartialUpdateParser.Parse(json.RootElement);

        Assert.True(parsed.Title.IsSpecified);
        Assert.Equal("Renamed", parsed.Title.Value);
        Assert.False(parsed.VirtualPath.IsSpecified);
        Assert.True(parsed.Content.IsSpecified);
        Assert.Equal(string.Empty, parsed.Content.Value);
        Assert.True(parsed.Tags.IsSpecified);
        Assert.Empty(parsed.Tags.Value!);
    }

    [Fact]
    public void PartialUpdateParserRejectsExplicitNullAndUnknownFields()
    {
        using var explicitNull = JsonDocument.Parse(
            """
            {
              "content": null
            }
            """);
        var nullError = Assert.Throws<McpToolRequestException>(
            () => McpPartialUpdateParser.Parse(explicitNull.RootElement));
        Assert.Equal("invalid_input", nullError.Code);

        using var unknown = JsonDocument.Parse(
            """
            {
              "unknown": true
            }
            """);
        var unknownError = Assert.Throws<McpToolRequestException>(
            () => McpPartialUpdateParser.Parse(unknown.RootElement));
        Assert.Equal("invalid_input", unknownError.Code);
    }

    [Fact]
    public void DocumentMapProjectorBoundsTextCollectionsAndResponse()
    {
        var headings = Enumerable.Range(0, 600)
            .Select(index => new DocumentHeading(
                1,
                new string('H', 1500) + index,
                [new string('P', 300)]))
            .ToArray();
        var map = new DocumentMap(
            new DocumentMetadata(
                Guid.NewGuid(),
                "Map",
                "Map.md",
                VaultDocumentFormat.Markdown,
                1,
                [],
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch),
            headings,
            [],
            [],
            0,
            false,
            false,
            false);

        var projected = McpDocumentMapProjector.Project(map);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            projected,
            ApiJson.CreateOptions());

        Assert.True(projected.ResponseTruncated);
        Assert.True(projected.Map.HeadingsTruncated);
        Assert.Contains("headings.text", projected.TruncatedFields);
        Assert.Contains("headings.path", projected.TruncatedFields);
        Assert.True(bytes.Length <= 64 * 1024);
    }
}
