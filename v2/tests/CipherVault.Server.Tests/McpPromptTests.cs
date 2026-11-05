using CipherVault.Server.Mcp;

namespace CipherVault.Server.Tests;

public sealed class McpPromptTests
{
    [Fact]
    public async Task ExposesTheSixKnowledgeWorkflowPrompts()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        unlock.EnsureSuccessStatusCode();
        var token = await McpTestClient.CreateTokenAsync(server.Client);

        var listed = await McpTestClient.ListPromptsAsync(
            server.Client,
            token);
        var prompts = listed.GetProperty("prompts")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("name").GetString()!,
                item => item.Clone(),
                StringComparer.Ordinal);
        Assert.Equal(
            new[]
            {
                McpPromptNames.AnswerWithKnowledge,
                McpPromptNames.ExtractSourceToNotes,
                McpPromptNames.ReviewKnowledgeQuality,
                McpPromptNames.ReviewPersonalProgress,
                McpPromptNames.SuggestKnowledgeRelations,
                McpPromptNames.SynthesizeSelectedKnowledge
            },
            prompts.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(
            prompts[McpPromptNames.ExtractSourceToNotes]
                .GetProperty("arguments")
                .EnumerateArray(),
            argument => argument.GetProperty("name").GetString() == "sourceId");

        var arguments = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            [McpPromptNames.AnswerWithKnowledge] = "question",
            [McpPromptNames.ExtractSourceToNotes] = "sourceId",
            [McpPromptNames.SynthesizeSelectedKnowledge] = "knowledgeIds",
            [McpPromptNames.SuggestKnowledgeRelations] = "knowledgeIds",
            [McpPromptNames.ReviewKnowledgeQuality] = "knowledgeId",
            [McpPromptNames.ReviewPersonalProgress] = "focus"
        };
        foreach (var (promptName, argumentName) in arguments)
        {
            var result = await McpTestClient.GetPromptAsync(
                server.Client,
                token,
                promptName,
                new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    [argumentName] = "test-value"
                });
            var text = result.GetProperty("messages")[0]
                .GetProperty("content")
                .GetProperty("text")
                .GetString();
            Assert.NotNull(text);
            Assert.Contains("MCP is Draft-only", text, StringComparison.Ordinal);
            Assert.Contains("test-value", text, StringComparison.Ordinal);
        }
    }
}
