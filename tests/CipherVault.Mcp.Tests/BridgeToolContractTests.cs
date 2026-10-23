using System.Text.Json;

namespace CipherVault.Mcp.Tests;

public sealed class BridgeToolContractTests
{
    private const string PassThroughResult =
        """{"ok":true,"document":{"documentId":"d80c2e83-636f-47a8-a3f2-e473beec9922"}}""";
    private static readonly IReadOnlySet<string> ReadOnlyScopes =
        ScopeSet("read");
    private static readonly IReadOnlySet<string> ReadWriteDeleteScopes =
        ScopeSet("read", "write", "delete");

    [Fact]
    public async Task BridgeToolsForwardAllNineToolContracts()
    {
        var bridge = new RecordingMcpBridgeClient();
        var tools = new BridgeVaultTools(bridge);
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
        var bridge = new RecordingMcpBridgeClient
        {
            RequestedScopes = ReadOnlyScopes,
            GrantedScopes = ReadOnlyScopes
        };
        var tools = new BridgeVaultTools(bridge);

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
            "access_denied",
            document.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        Assert.Empty(bridge.Calls);
    }

    [Theory]
    [InlineData("access_grant_required")]
    [InlineData("access_grant_revoked")]
    [InlineData("access_grant_expired")]
    public async Task GrantFailureTriggersOneReauthorizationAndRetry(
        string errorCode)
    {
        var bridge = new RecoveringMcpBridgeClient(errorCode);
        var tools = new BridgeVaultTools(bridge);

        var result = await tools.ReadNote(
            "d80c2e83-636f-47a8-a3f2-e473beec9922",
            0,
            100,
            CancellationToken.None);

        Assert.Equal(PassThroughResult, result);
        Assert.Equal(1, bridge.GrantRequests);
        Assert.Equal(2, bridge.Calls.Count);
        Assert.Equal("read_note", bridge.Calls[0].ToolName);
        Assert.Equal("read_note", bridge.Calls[1].ToolName);
    }

    [Fact]
    public async Task GrantFailureReturnsOriginalResultWhenReauthorizationFails()
    {
        var bridge = new RecoveringMcpBridgeClient(
            "access_grant_expired",
            failGrantRequest: true);
        var tools = new BridgeVaultTools(bridge);

        var result = await tools.VaultStatus(CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "access_grant_expired",
            document.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        Assert.Equal(1, bridge.GrantRequests);
        Assert.Single(bridge.Calls);
    }

    [Fact]
    public async Task ReauthorizationScopeNarrowingBlocksWriteBeforeRetry()
    {
        var bridge = new RecoveringMcpBridgeClient(
            "access_grant_required",
            grantedScopesAfterRequest: ReadOnlyScopes);
        var tools = new BridgeVaultTools(bridge);

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
            "access_denied",
            document.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        Assert.Equal(1, bridge.GrantRequests);
        Assert.Single(bridge.Calls);
    }

    [Fact]
    public void ToolAvailabilityFollowsGrantedScopes()
    {
        Assert.True(BridgeVaultTools.IsToolAvailable(
            "read_note",
            ReadOnlyScopes));
        Assert.False(BridgeVaultTools.IsToolAvailable(
            "create_note",
            ReadOnlyScopes));
        Assert.True(BridgeVaultTools.IsToolAvailable(
            "create_note",
            ReadWriteDeleteScopes));
        Assert.True(BridgeVaultTools.IsToolAvailable(
            "delete_note",
            ReadWriteDeleteScopes));
        Assert.False(BridgeVaultTools.IsToolAvailable(
            "delete_note",
            ScopeSet("read", "write")));
    }

    private sealed class RecordingMcpBridgeClient : IMcpBridgeClient
    {
        public List<(
            string ToolName,
            IReadOnlyDictionary<string, object?> Arguments)> Calls
        { get; } = [];
        public bool Revoked { get; private set; }
        public IReadOnlySet<string> RequestedScopes { get; init; } =
            ReadWriteDeleteScopes;
        public IReadOnlySet<string> GrantedScopes { get; init; } =
            ReadWriteDeleteScopes;

        public Task<string> CallToolAsync(
            string name,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            Calls.Add((name, arguments));
            return Task.FromResult(PassThroughResult);
        }

        public Task RequestGrantAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task RevokeGrantAsync(
            CancellationToken cancellationToken = default)
        {
            Revoked = true;
            return Task.CompletedTask;
        }
    }

    private sealed class RecoveringMcpBridgeClient : IMcpBridgeClient
    {
        private readonly string _errorCode;
        private readonly bool _failGrantRequest;
        private readonly IReadOnlySet<string>? _grantedScopesAfterRequest;

        public RecoveringMcpBridgeClient(
            string errorCode,
            bool failGrantRequest = false,
            IReadOnlySet<string>? grantedScopesAfterRequest = null)
        {
            _errorCode = errorCode;
            _failGrantRequest = failGrantRequest;
            _grantedScopesAfterRequest = grantedScopesAfterRequest;
        }

        public List<(
            string ToolName,
            IReadOnlyDictionary<string, object?> Arguments)> Calls
        { get; } = [];
        public int GrantRequests { get; private set; }
        public IReadOnlySet<string> RequestedScopes { get; } =
            ReadWriteDeleteScopes;
        public IReadOnlySet<string> GrantedScopes { get; private set; } =
            ReadWriteDeleteScopes;

        public Task<string> CallToolAsync(
            string name,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            Calls.Add((name, arguments));
            var result = Calls.Count == 1
                ? JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = new
                    {
                        code = _errorCode,
                        message = "The Bridge Access Grant is no longer valid."
                    }
                })
                : PassThroughResult;
            return Task.FromResult(result);
        }

        public Task RequestGrantAsync(
            CancellationToken cancellationToken = default)
        {
            GrantRequests++;
            if (_failGrantRequest)
            {
                throw new InvalidOperationException("Bridge is unavailable.");
            }
            if (_grantedScopesAfterRequest is not null)
            {
                GrantedScopes = _grantedScopesAfterRequest;
            }
            return Task.CompletedTask;
        }

        public Task RevokeGrantAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private static IReadOnlySet<string> ScopeSet(params string[] scopes)
    {
        return new HashSet<string>(scopes, StringComparer.Ordinal);
    }
}
