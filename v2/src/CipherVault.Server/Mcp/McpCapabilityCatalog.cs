namespace CipherVault.Server.Mcp;

internal sealed record McpToolCapabilityInfo(
    string Name,
    string Group,
    string Summary);

internal sealed record McpPromptCapabilityInfo(
    string Name,
    string Title,
    string Summary);

internal static class McpCapabilityCatalog
{
    internal const string Transport = "streamable_http";
    internal const string EndpointPath = "/mcp";

    internal static IReadOnlyList<McpToolCapabilityInfo> Tools { get; } =
    [
        new(
            McpToolNames.VaultStatus,
            "security",
            "Read the current Vault session status."),
        new(
            McpToolNames.GetKnowledgeModel,
            "model",
            "Read the current configurable Knowledge Model."),
        new(
            McpToolNames.ListSources,
            "sources",
            "List source metadata without content."),
        new(
            McpToolNames.ReadSource,
            "sources",
            "Read source metadata and a bounded content chunk."),
        new(
            McpToolNames.ListSourceRevisions,
            "sources",
            "List immutable source revisions."),
        new(
            McpToolNames.ListSourceLocators,
            "sources",
            "List section and paragraph locators for a source revision."),
        new(
            McpToolNames.ReadSourceLocator,
            "sources",
            "Read a bounded source locator range."),
        new(
            McpToolNames.SearchKnowledge,
            "knowledge",
            "Search Knowledge using keyword or fulltext mode."),
        new(
            McpToolNames.ReadKnowledge,
            "knowledge",
            "Read Knowledge metadata and a bounded content chunk."),
        new(
            McpToolNames.GetLocalGraph,
            "knowledge",
            "Read a bounded local relation graph."),
        new(
            McpToolNames.ListDrafts,
            "drafts",
            "List review drafts."),
        new(
            McpToolNames.ReadDraft,
            "drafts",
            "Read draft metadata, preview, and source evidence."),
        new(
            McpToolNames.CreateDraft,
            "drafts",
            "Create a Draft without applying formal data."),
        new(
            McpToolNames.UpdateDraft,
            "drafts",
            "Update an editable Draft."),
        new(
            McpToolNames.RebaseDraft,
            "drafts",
            "Rebase a Draft against current target state."),
        new(
            McpToolNames.SubmitDraftForReview,
            "drafts",
            "Submit a Draft for the Web user to review."),
        new(
            McpToolNames.DiscardDraft,
            "drafts",
            "Discard an editable Draft.")
    ];

    internal static IReadOnlyList<McpPromptCapabilityInfo> Prompts { get; } =
    [
        new(
            McpPromptNames.AnswerWithKnowledge,
            "Answer with Knowledge",
            "Answer from retrieved Knowledge with citations and uncertainty."),
        new(
            McpPromptNames.ExtractSourceToNotes,
            "Extract Source to Notes",
            "Create bounded Knowledge drafts from an immutable source revision."),
        new(
            McpPromptNames.SynthesizeSelectedKnowledge,
            "Synthesize Selected Knowledge",
            "Synthesize multiple Knowledge records without losing evidence."),
        new(
            McpPromptNames.SuggestKnowledgeRelations,
            "Suggest Knowledge Relations",
            "Propose evidence-backed relation drafts."),
        new(
            McpPromptNames.ReviewKnowledgeQuality,
            "Review Knowledge Quality",
            "Review evidence, structure, duplicates, and model compliance."),
        new(
            McpPromptNames.ReviewPersonalProgress,
            "Review Personal Progress",
            "Summarize plans, progress, blockers, and next actions.")
    ];
}
