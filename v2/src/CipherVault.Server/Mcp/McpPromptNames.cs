namespace CipherVault.Server.Mcp;

public static class McpPromptNames
{
    public const string AnswerWithKnowledge = "answer_with_knowledge";
    public const string ExtractSourceToNotes = "extract_source_to_notes";
    public const string SynthesizeSelectedKnowledge =
        "synthesize_selected_knowledge";
    public const string SuggestKnowledgeRelations =
        "suggest_knowledge_relations";
    public const string ReviewKnowledgeQuality = "review_knowledge_quality";
    public const string ReviewPersonalProgress = "review_personal_progress";

    public static IReadOnlyList<string> All { get; } =
    [
        AnswerWithKnowledge,
        ExtractSourceToNotes,
        SynthesizeSelectedKnowledge,
        SuggestKnowledgeRelations,
        ReviewKnowledgeQuality,
        ReviewPersonalProgress
    ];
}
