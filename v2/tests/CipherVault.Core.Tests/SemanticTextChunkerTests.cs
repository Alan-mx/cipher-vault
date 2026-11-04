using CipherVault.Core.Documents;
using CipherVault.Core.Semantic;

namespace CipherVault.Core.Tests;

public sealed class SemanticTextChunkerTests
{
    [Fact]
    public void MarkdownChunksPreserveHeadingPathOffsetsAndPrefix()
    {
        const string content =
            """
            ---
            project: CipherVault
            type: technical-note
            maturity: verified
            ---

            # Cache

            First paragraph.

            ## Consistency

            Second paragraph.
            """;
        var chunks = SemanticTextChunker.Chunk(
            new SemanticChunkingRequest(
                Guid.NewGuid(),
                3,
                "Cache Design",
                VaultDocumentFormat.Markdown,
                content,
                ["Redis"],
                new Dictionary<string, string>
                {
                    ["project"] = "CipherVault",
                    ["type"] = "technical-note",
                    ["maturity"] = "verified",
                    ["ignored"] = "not-sent"
                }),
            maxInputTokens: 2048,
            value => value.Length);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(["Cache"], chunks[0].HeadingPath);
        Assert.Equal(["Cache", "Consistency"], chunks[1].HeadingPath);
        Assert.Contains("Title: Cache Design", chunks[0].ProviderInput);
        Assert.Contains("project: CipherVault", chunks[0].ProviderInput);
        Assert.Contains("Tags: Redis", chunks[0].ProviderInput);
        Assert.DoesNotContain("ignored", chunks[0].ProviderInput);
        foreach (var chunk in chunks)
        {
            Assert.Equal(
                chunk.Content,
                content[chunk.StartOffset..chunk.EndOffset]);
            Assert.Equal(
                SemanticProfileIdentity.Sha256Hex(chunk.Content),
                chunk.ContentHash);
        }
    }

    [Fact]
    public void LongTextUsesOverlapAndStaysUnderTokenLimit()
    {
        var content = string.Concat(
            Enumerable.Repeat("缓存一致性😀", 500));
        var chunks = SemanticTextChunker.Chunk(
            new SemanticChunkingRequest(
                Guid.NewGuid(),
                1,
                "Long",
                VaultDocumentFormat.Text,
                content,
                [],
                new Dictionary<string, string>()),
            maxInputTokens: 180,
            value => value.Length);

        Assert.True(chunks.Count > 2);
        Assert.All(chunks, chunk =>
        {
            Assert.True(chunk.ProviderInput.Length <= 180);
            Assert.Empty(chunk.HeadingPath);
            Assert.Equal(
                chunk.Content,
                content[chunk.StartOffset..chunk.EndOffset]);
            Assert.False(char.IsLowSurrogate(chunk.Content[0]));
        });
        for (var index = 1; index < chunks.Count; index++)
        {
            Assert.True(
                chunks[index].StartOffset < chunks[index - 1].EndOffset,
                $"chunk {index}: {chunks[index].StartOffset} >= "
                + $"{chunks[index - 1].EndOffset}; "
                + $"lengths={chunks[index - 1].Content.Length},"
                + $"{chunks[index].Content.Length}; "
                + $"firstStart={chunks[0].StartOffset}; "
                + $"char16IsWhite={char.IsWhiteSpace(content[16])}; "
                + $"char16={(int)content[16]}");
        }
    }

    [Fact]
    public void VectorCodecNormalizesUsesLittleEndianAndRejectsInvalidValues()
    {
        var blob = SemanticVectorCodec.NormalizeAndEncode([3f, 4f]);
        Assert.Equal(8, blob.Length);
        Assert.Equal(
            0.6f,
            System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(
                blob.AsSpan(0, 4)),
            precision: 6);
        var decoded = SemanticVectorCodec.Decode(blob, 2);
        Assert.Equal(1d, SemanticVectorCodec.Dot(decoded, decoded), precision: 6);

        Assert.Throws<ArgumentException>(
            () => SemanticVectorCodec.NormalizeAndEncode([0f, 0f]));
        Assert.Throws<ArgumentException>(
            () => SemanticVectorCodec.NormalizeAndEncode([float.NaN, 1f]));
        Assert.Throws<ArgumentException>(
            () => SemanticVectorCodec.NormalizeAndEncode([float.PositiveInfinity]));
    }
}
