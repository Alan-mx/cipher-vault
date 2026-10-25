using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Host.Client;
using CipherVault.Host.Mcp;
using CipherVault.Host.Security;

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
                updateBacklinks: true,
                cancellationToken: cancellationToken));
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
                ["tags"] = new[] { "tag" },
                ["updateBacklinks"] = true
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
    public void UpdateNoteMapperForwardsBacklinkRewriteOption()
    {
        Assert.True(
            BridgeToolCatalog.TryGet("update_note", out var definition));
        var mapped = BridgeToolMapper.Map(
            definition,
            JsonSerializer.SerializeToElement(new
            {
                documentId = "d80c2e83-636f-47a8-a3f2-e473beec9922",
                expectedRevision = 3,
                updateBacklinks = true
            }));

        Assert.Equal("admin.notes.update", mapped.AdminMethod);
        Assert.True(
            mapped.AdminPayload
                .GetProperty("request")
                .GetProperty("updateBacklinks")
                .GetBoolean());
    }

    [Fact]
    public async Task KnowledgeToolsForwardStructuredContractsAndAnnotations()
    {
        var bridge = new RecordingMcpBridgeClient();
        var tools = new BridgeVaultTools(bridge);
        const string id = "d80c2e83-636f-47a8-a3f2-e473beec9922";

        await tools.TagList("tag-cursor", 25);
        await tools.GetDocumentMap(id, 80, 40, 20);
        await tools.GetBacklinks(id, "backlink-cursor", 10);
        await tools.FindUnlinkedMentions(id, "mention-cursor", 15);
        await tools.GetLocalGraph(
            id,
            2,
            ["tag"],
            "folder",
            100,
            250);
        await tools.PatchNote(
            id,
            5,
            "heading",
            "append",
            ["Heading"],
            content: "text",
            dryRun: true);

        Assert.Equal(
            [
                "tag_list",
                "get_document_map",
                "get_backlinks",
                "find_unlinked_mentions",
                "get_local_graph",
                "patch_note"
            ],
            bridge.Calls.Select(call => call.ToolName));
        Assert.Equal(25, bridge.Calls[0].Arguments["limit"]);
        Assert.Equal(id, bridge.Calls[1].Arguments["documentId"]);
        Assert.Equal(80, bridge.Calls[1].Arguments["maxHeadings"]);
        Assert.Equal(40, bridge.Calls[1].Arguments["maxFrontMatterFields"]);
        Assert.Equal(20, bridge.Calls[1].Arguments["maxLinks"]);
        Assert.Equal(10, bridge.Calls[2].Arguments["limit"]);
        Assert.Equal(15, bridge.Calls[3].Arguments["limit"]);
        Assert.Equal(2, bridge.Calls[4].Arguments["depth"]);
        Assert.Equal(5, bridge.Calls[5].Arguments["expectedRevision"]);
        Assert.Equal(true, bridge.Calls[5].Arguments["dryRun"]);

        var readAnnotations = BridgeVaultTools.AnnotationsFor("get_backlinks");
        Assert.True(readAnnotations.ReadOnlyHint);
        Assert.False(readAnnotations.DestructiveHint);
        Assert.True(readAnnotations.IdempotentHint);
        Assert.False(readAnnotations.OpenWorldHint);

        var writeAnnotations = BridgeVaultTools.AnnotationsFor("patch_note");
        Assert.False(writeAnnotations.ReadOnlyHint);
        Assert.True(writeAnnotations.DestructiveHint);
        Assert.False(writeAnnotations.IdempotentHint);
    }

    [Fact]
    public void UnlinkedMentionsMapperUsesReadOnlyAdminContract()
    {
        Assert.True(
            BridgeToolCatalog.TryGet("find_unlinked_mentions", out var definition));
        Assert.Equal(AccessGrantScope.Read, definition.RequiredScope);
        var mapped = BridgeToolMapper.Map(
            definition,
            JsonSerializer.SerializeToElement(new
            {
                documentId = "d80c2e83-636f-47a8-a3f2-e473beec9922",
                cursor = "cursor",
                limit = 15
            }));

        Assert.Equal("admin.notes.unlinked-mentions", mapped.AdminMethod);
        Assert.Equal(
            "cursor",
            mapped.AdminPayload.GetProperty("cursor").GetString());
        Assert.Equal(
            15,
            mapped.AdminPayload.GetProperty("limit").GetInt32());
        var annotations = BridgeVaultTools.AnnotationsFor(
            "find_unlinked_mentions");
        Assert.True(annotations.ReadOnlyHint);
        Assert.False(annotations.DestructiveHint);
        Assert.True(annotations.IdempotentHint);
        Assert.False(annotations.OpenWorldHint);
    }

    [Fact]
    public void DocumentMapMapperAndLimiterBoundEachCollection()
    {
        Assert.True(
            BridgeToolCatalog.TryGet("get_document_map", out var definition));
        var mapped = BridgeToolMapper.Map(
            definition,
            JsonSerializer.SerializeToElement(new
            {
                documentId = "d80c2e83-636f-47a8-a3f2-e473beec9922",
                maxHeadings = 1,
                maxFrontMatterFields = 1,
                maxLinks = 2
            }));
        Assert.Equal(1, mapped.AdminPayload.GetProperty("maxHeadings").GetInt32());
        Assert.Equal(
            1,
            mapped.AdminPayload.GetProperty("maxFrontMatterFields").GetInt32());
        Assert.Equal(2, mapped.AdminPayload.GetProperty("maxLinks").GetInt32());

        var map = new VaultDocumentMap(
            Guid.NewGuid(),
            "title",
            "folder/title.md",
            "md",
            3,
            [
                new VaultDocumentHeading(1, "one", ["one"]),
                new VaultDocumentHeading(2, "two", ["one", "two"])
            ],
            [
                new VaultFrontMatterField("one", "string", "\"one\""),
                new VaultFrontMatterField("two", "string", "\"two\"")
            ],
            [
                new VaultOutgoingLink(
                    "a",
                    "a",
                    null,
                    "references",
                    "unresolved",
                    null,
                    [],
                    0,
                    5),
                new VaultOutgoingLink(
                    "b",
                    "b",
                    null,
                    "references",
                    "unresolved",
                    null,
                    [],
                    6,
                    11),
                new VaultOutgoingLink(
                    "c",
                    "c",
                    null,
                    "references",
                    "unresolved",
                    null,
                    [],
                    12,
                    17)
            ],
            0,
            DateTimeOffset.UtcNow);

        var limited = BridgeDocumentMapLimiter.Apply(map, 1, 1, 2);

        Assert.Single(limited.Headings);
        Assert.Single(limited.FrontMatterFields);
        Assert.Equal(2, limited.OutgoingLinks.Count);
        Assert.True(limited.HeadingsTruncated);
        Assert.True(limited.FrontMatterTruncated);
        Assert.True(limited.LinksTruncated);
        var full = BridgeDocumentMapLimiter.Apply(map, null, null, null);
        Assert.Equal(2, full.Headings.Count);
        Assert.Equal(2, full.FrontMatterFields.Count);
        Assert.Equal(3, full.OutgoingLinks.Count);
        Assert.False(full.HeadingsTruncated);
    }

    [Fact]
    public void LocalMcpErrorsIncludeRecoveryMetadata()
    {
        var json = McpToolErrorJson.Serialize(
            "access_denied",
            "The current Access Grant does not allow write.");

        using var document = JsonDocument.Parse(json);
        var error = document.RootElement.GetProperty("error");
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Contains(
            "required scope",
            error.GetProperty("hint").GetString(),
            StringComparison.Ordinal);
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
    public async Task GrantFailureReturnsReauthorizationErrorWhenRecoveryFails()
    {
        var bridge = new RecoveringMcpBridgeClient(
            "access_grant_expired",
            failGrantRequest: true);
        var tools = new BridgeVaultTools(bridge);

        var result = await tools.VaultStatus(CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "bridge_reauthorization_failed",
            document.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        Assert.Equal(1, bridge.GrantRequests);
        Assert.Single(bridge.Calls);
    }

    [Fact]
    public async Task GrantFailureReturnsBrokerErrorCodeWhenRecoveryFails()
    {
        var bridge = new RecoveringMcpBridgeClient(
            "access_grant_required",
            grantFailure: new PipeRpcException(
                "approval_broker_unavailable",
                "The interactive Auth Broker is not available."));
        var tools = new BridgeVaultTools(bridge);

        var result = await tools.VaultStatus(CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "approval_broker_unavailable",
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
        private readonly Exception? _grantFailure;
        private readonly IReadOnlySet<string>? _grantedScopesAfterRequest;

        public RecoveringMcpBridgeClient(
            string errorCode,
            bool failGrantRequest = false,
            IReadOnlySet<string>? grantedScopesAfterRequest = null,
            Exception? grantFailure = null)
        {
            _errorCode = errorCode;
            _failGrantRequest = failGrantRequest;
            _grantedScopesAfterRequest = grantedScopesAfterRequest;
            _grantFailure = grantFailure;
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
            if (_grantFailure is not null)
            {
                throw _grantFailure;
            }
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
