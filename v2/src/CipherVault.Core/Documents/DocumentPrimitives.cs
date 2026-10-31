using System.Text.Json;

namespace CipherVault.Core.Documents;

public sealed record VaultDocumentHeading(
    int Level,
    string Text,
    IReadOnlyList<string> Path);

public sealed record VaultFrontMatterFieldView(
    string Key,
    string ValueType,
    string ValueJson);

public sealed record VaultUnlinkedMentionOccurrence(
    int StartOffset,
    int EndOffset,
    string Context);

internal sealed record VaultPatchCommand(
    string TargetKind,
    IReadOnlyList<string>? HeadingPath,
    string? FrontMatterKey,
    string Operation,
    string? ContentMarkdown,
    JsonElement? Value,
    bool DryRun);
