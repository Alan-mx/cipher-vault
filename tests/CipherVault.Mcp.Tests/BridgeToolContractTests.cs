using System.Text.Json;

namespace CipherVault.Mcp.Tests;

public sealed class BridgeToolContractTests
{
    private const string PassThroughResult =
        """{"ok":true,"document":{"documentId":"d80c2e83-636f-47a8-a3f2-e473beec9922"}}""";

    [Fact]
    public async Task BridgeToolsForwardAllNineToolContracts()
    {
        var bridge = new RecordingMcpBridgeClient();
        var tools = new BridgeVaultTools(bridge, readOnly: false);
        var cancellationToken = CancellationToken.None;

        Assert.Equal(
            PassThroughResult,
            await tools.VaultStatus(cancellationToken));
        Assert.Equal(
            PassThroughResult,
            await tools.ListCatalog("folder", "cursor", 25, cancellationToken));
        Assert.Equal(
            PassThroughResult,
            await tools.SearchNotes("query", "folder", "cursor", 5, cancellationToken));
        Assert.Equal(
            PassThroughResult,
            await tools.ReadNote("d80c2e83-636f-47a8-a3f2-e473beec9922", 10, 500, cancellationToken));
        Assert.Equal(
            PassThroughResult,
            await tools.ScanNotes("cursor", 20, cancellationToken));
        Assert.Equal(
            PassThroughResult,
            await tools.CreateNote(
                "title",
                "content",
                "folder",
                "md",
                ["tag"],
                cancellationToken));
        Assert.Equal(
            PassThroughResult,
            await tools.AppendNote(
                "d80c2e83-636f-47a8-a3f2-e473beec9922",
                2,
                "append",
                cancellationToken));
        Assert.Equal(
            PassThroughResult,
            await tools.UpdateNote(
                "d80c2e83-636f-47a8-a3f2-e473beec9922",
                3,
                "title",
                "folder",
                "content",
                ["tag"],
                cancellationToken));
        Assert.Equal(
            PassThroughResult,
            await tools.DeleteNote(
                "d80c2e83-636f-47a8-a3f2-e473beec9922",
                4,
                cancellationToken));
        var expected = new[]
        {
            ("vault_status", (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>()),
            ("list_catalog", (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["folder"] = "folder",
                ["cursor"] = "cursor",
                ["limit"] = 25
            }),
            ("search_notes", (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["query"] = "query",
                ["folder"] = "folder",
                ["cursor"] = "cursor",
                ["limit"] = 5
            }),
            ("read_note", (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["documentId"] = "d80c2e83-636f-47a8-a3f2-e473beec9922",
                ["offset"] = 10,
                ["maxChars"] = 500
            }),
            ("scan_notes", (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["cursor"] = "cursor",
                ["limit"] = 20
            }),
            ("create_note", (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["title"] = "title",
                ["content"] = "content",
                ["folder"] = "folder",
                ["format"] = "md",
                ["tags"] = new[] { "tag" }
            }),
            ("append_note", (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["documentId"] = "d80c2e83-636f-47a8-a3f2-e473beec9922",
                ["expectedRevision"] = 2,
                ["content"] = "append"
            }),
            ("update_note", (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["documentId"] = "d80c2e83-636f-47a8-a3f2-e473beec9922",
                ["expectedRevision"] = 3,
                ["title"] = "title",
                ["folder"] = "folder",
                ["content"] = "content",
                ["tags"] = new[] { "tag" }
            }),
            ("delete_note", (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["documentId"] = "d80c2e83-636f-47a8-a3f2-e473beec9922",
                ["expectedRevision"] = 4
            })
        };

        Assert.Equal(
            expected.Select(item => item.Item1),
            bridge.Calls.Select(item => item.ToolName));
        foreach (var (actual, expectedArguments) in bridge.Calls.Zip(expected))
        {
            Assert.Equal(
                expectedArguments.Item2.Keys,
                actual.Arguments.Keys);
            foreach (var key in expectedArguments.Item2.Keys)
            {
                Assert.Equal(
                    expectedArguments.Item2[key],
                    actual.Arguments[key]);
            }
        }
    }

    [Fact]
    public async Task ReadOnlyBridgeBlocksWriteWithoutCallingBridge()
    {
        var bridge = new RecordingMcpBridgeClient();
        var tools = new BridgeVaultTools(bridge, readOnly: true);

        var result = await tools.CreateNote(
            "title",
            "content",
            folder: null,
            format: "md",
            tags: null,
            CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "read_only",
            document.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        Assert.Empty(bridge.Calls);
    }

    private sealed class RecordingMcpBridgeClient : IMcpBridgeClient
    {
        public List<(
            string ToolName,
            IReadOnlyDictionary<string, object?> Arguments)> Calls
        { get; } = [];
        public bool Revoked { get; private set; }

        public Task<string> CallToolAsync(
            string name,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            Calls.Add((name, arguments));
            return Task.FromResult(PassThroughResult);
        }

        public Task RevokeGrantAsync(
            CancellationToken cancellationToken = default)
        {
            Revoked = true;
            return Task.CompletedTask;
        }
    }
}
