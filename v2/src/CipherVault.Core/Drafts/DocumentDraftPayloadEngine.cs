using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Drafts;

public static class DocumentDraftPayloadEngine
{
    internal const int MaxOperations = 32;
    internal const int MaxPayloadBytes = 4 * 1024 * 1024;
    internal const int MaxAppendBytes = 256 * 1024;
    private static readonly Regex PropertyKeyPattern = new(
        "^[A-Za-z_][A-Za-z0-9_.-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private static readonly HashSet<string> CreatePayloadKeys =
        new(StringComparer.Ordinal)
        {
            "title",
            "virtualPath",
            "folder",
            "format",
            "content",
            "tags",
            "reviewContext"
        };
    private static readonly HashSet<string> UpdatePayloadKeys =
        new(StringComparer.Ordinal)
        {
            "operations",
            "reviewContext"
        };
    private static readonly HashSet<string> ReplaceBodyKeys =
        OperationKeys("replace_body", "content");
    private static readonly HashSet<string> AppendBodyKeys =
        OperationKeys("append_body", "content");
    private static readonly HashSet<string> SetPropertyKeys =
        OperationKeys("set_property", "key", "value");
    private static readonly HashSet<string> RemovePropertyKeys =
        OperationKeys("remove_property", "key");
    private static readonly HashSet<string> AddTagKeys =
        OperationKeys("add_tag", "tag");
    private static readonly HashSet<string> RemoveTagKeys =
        OperationKeys("remove_tag", "tag");
    private static readonly string[] OperationTypeOrder =
    [
        "replace_body",
        "append_body",
        "set_property",
        "remove_property",
        "add_tag",
        "remove_tag"
    ];

    internal static PreparedCreateDraftPayload PrepareCreatePayload(
        JsonElement payload,
        Guid futureDocumentId,
        bool allowMatchingIdentity = false,
        JsonElement? existingPayload = null)
    {
        RequireObject(payload, "Create draft payload");
        if (payload.TryGetProperty("futureDocumentId", out _)
            || payload.TryGetProperty("payloadVersion", out _))
        {
            throw new VaultException(
                VaultErrorCode.DocumentIdProtected,
                "Create draft system identity fields are managed by the server.");
        }
        DocumentDraftReviewContextCodec.EnsureOnlyProperties(
            payload,
            "Create draft payload",
            CreatePayloadKeys);
        DocumentDraftReviewContextCodec.ValidateFromPayload(payload);
        var source = existingPayload is { } existing
            ? DocumentDraftReviewContextCodec.MergeForUpdate(payload, existing)
            : payload;
        var title = RequireString(source, "title", allowEmpty: false);
        var content = RequireString(source, "content", allowEmpty: true);
        var format = ParseFormat(RequireString(source, "format", allowEmpty: false));
        var virtualPath = OptionalString(source, "virtualPath");
        var folder = OptionalString(source, "folder");
        var tags = OptionalStringArray(source, "tags");
        if (format == VaultDocumentFormat.Markdown)
        {
            var identity = DocumentIdentity.InspectMarkdown(content);
            if (identity.HasId)
            {
                if (!allowMatchingIdentity)
                {
                    throw new VaultException(
                        VaultErrorCode.DocumentIdProtected,
                        "A create draft cannot specify Frontmatter id.");
                }
                if (identity.DocumentId != futureDocumentId)
                {
                    throw new VaultException(
                        VaultErrorCode.DocumentIdMismatch,
                        "Create draft Frontmatter id does not match futureDocumentId.");
                }
            }
            else
            {
                content = DocumentIdentity.EnsureMarkdownIdentity(
                    content,
                    futureDocumentId,
                    DocumentIdentityMode.NewDocument);
            }
        }
        var parsed = DocumentContentParser.Parse(
            title,
            virtualPath,
            folder,
            format,
            content,
            tags);
        var working = new DocumentDraftWorkingCopy(
            futureDocumentId,
            parsed.Title,
            parsed.Path.Path,
            format,
            parsed.Content,
            parsed.Tags.Select(tag => tag.Name).ToArray());
        var persistedPayload = JsonSerializer.SerializeToElement(
            new
            {
                payloadVersion = 2,
                futureDocumentId = DocumentIdentity.Format(futureDocumentId),
                title = working.Title,
                virtualPath = working.VirtualPath,
                format = FormatToDatabaseValue(working.Format),
                content = working.Content,
                tags = working.Tags
            },
            JsonOptions);
        var reviewContext =
            DocumentDraftReviewContextCodec.NormalizeForCreate(source);
        if (reviewContext is { } context)
        {
            var node = JsonNode.Parse(persistedPayload.GetRawText())
                as JsonObject
                ?? throw CorruptDraft(
                    "Create draft payload could not be normalized.");
            node["reviewContext"] = JsonNode.Parse(context.GetRawText());
            persistedPayload = JsonSerializer.SerializeToElement(node, JsonOptions);
        }
        return new PreparedCreateDraftPayload(
            futureDocumentId,
            persistedPayload,
            working);
    }

    public static DocumentDraftWorkingCopy ParseCreatePayload(
        JsonElement payload)
    {
        RequireObject(payload, "Create draft payload");
        if (!payload.TryGetProperty("payloadVersion", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var payloadVersion)
            || payloadVersion != 2
            || !payload.TryGetProperty("futureDocumentId", out var documentIdValue)
            || documentIdValue.ValueKind != JsonValueKind.String
            || !DocumentIdentity.TryParse(
                documentIdValue.GetString(),
                out var futureDocumentId))
        {
            throw CorruptDraft(
                "Create draft payload version 2 or futureDocumentId is invalid.");
        }
        var title = RequireString(payload, "title", allowEmpty: false);
        var content = RequireString(payload, "content", allowEmpty: true);
        var format = ParseFormat(RequireString(payload, "format", allowEmpty: false));
        var virtualPath = OptionalString(payload, "virtualPath")
            ?? throw CorruptDraft("Create draft virtualPath is missing.");
        var tags = OptionalStringArray(payload, "tags");
        var parsed = DocumentContentParser.Parse(
            title,
            virtualPath,
            folder: null,
            format,
            content,
            tags);
        if (format == VaultDocumentFormat.Markdown)
        {
            var identity = DocumentIdentity.InspectMarkdown(parsed.Content);
            if (!identity.HasId)
            {
                throw CorruptDraft(
                    "Create draft Markdown is missing Frontmatter id.");
            }
            if (identity.DocumentId != futureDocumentId)
            {
                throw CorruptDraft(
                    "Create draft Frontmatter id does not match futureDocumentId.");
            }
        }
        return new DocumentDraftWorkingCopy(
            futureDocumentId,
            parsed.Title,
            parsed.Path.Path,
            format,
            parsed.Content,
            parsed.Tags.Select(tag => tag.Name).ToArray());
    }

    public static Guid RequireFutureDocumentId(DocumentDraftRecord draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.DraftType != DocumentDraftType.Create)
        {
            throw CorruptDraft("Only create drafts have a futureDocumentId.");
        }
        using var document = JsonDocument.Parse(draft.PayloadJson);
        var working = ParseCreatePayload(document.RootElement);
        return working.DocumentId!.Value;
    }

    public static JsonElement ToClientPayload(DocumentDraftRecord draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var node = JsonNode.Parse(draft.PayloadJson) as JsonObject
            ?? throw CorruptDraft("Draft payload must be a JSON object.");
        if (draft.DraftType == DocumentDraftType.Create)
        {
            _ = node.Remove("payloadVersion");
            _ = node.Remove("futureDocumentId");
            if (node["content"] is JsonValue contentValue
                && contentValue.TryGetValue<string>(out var content)
                && node["format"] is JsonValue formatValue
                && formatValue.TryGetValue<string>(out var format)
                && ParseFormat(format) == VaultDocumentFormat.Markdown)
            {
                node["content"] = DocumentIdentity.RemoveMarkdownIdentity(
                    content);
            }
        }
        return JsonSerializer.SerializeToElement(node, JsonOptions);
    }

    public static void ValidateUpdatePayload(JsonElement payload)
    {
        _ = ParseOperations(payload);
    }

    public static JsonElement PrepareUpdatePayload(
        JsonElement payload,
        JsonElement existingPayload)
    {
        _ = ParseOperations(payload);
        return DocumentDraftReviewContextCodec.MergeForUpdate(
            payload,
            existingPayload);
    }

    public static void ValidateTrashPayload(JsonElement payload)
    {
        _ = DocumentDraftReviewContextCodec.NormalizeTrashPayload(payload);
    }

    public static JsonElement NormalizeTrashPayload(JsonElement payload) =>
        DocumentDraftReviewContextCodec.NormalizeTrashPayload(payload);

    public static JsonElement PrepareTrashPayload(
        JsonElement incomingPayload,
        JsonElement existingPayload)
    {
        var merged = DocumentDraftReviewContextCodec.MergeForUpdate(
            incomingPayload,
            existingPayload);
        return DocumentDraftReviewContextCodec.NormalizeTrashPayload(merged);
    }

    public static DocumentDraftPreviewComputation ComputePreview(
        DocumentDraftRecord draft,
        JsonElement payload,
        DocumentDraftSnapshot? baseSnapshot)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft.DraftType switch
        {
            DocumentDraftType.Create => ComputeCreate(draft.PayloadJson),
            DocumentDraftType.Update => ComputeUpdate(
                payload,
                baseSnapshot ?? throw CorruptDraft(
                    "Update draft is missing its base snapshot.")),
            DocumentDraftType.Trash => ComputeTrash(
                payload,
                baseSnapshot ?? throw CorruptDraft(
                    "Trash draft is missing its base snapshot.")),
            _ => throw new ArgumentOutOfRangeException(nameof(draft))
        };
    }

    public static string SerializeSnapshot(DocumentDraftSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(
            new
            {
                documentId = snapshot.DocumentId,
                revision = snapshot.Revision,
                title = snapshot.Title,
                virtualPath = snapshot.VirtualPath,
                format = FormatToDatabaseValue(snapshot.Format),
                content = snapshot.Content,
                tags = snapshot.Tags
            },
            JsonOptions);
    }

    public static DocumentDraftSnapshot ParseSnapshot(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw CorruptDraft(
                    "Draft base snapshot must be a JSON object.");
            }
            if (!Guid.TryParse(
                    RequireString(root, "documentId", allowEmpty: false),
                    out var documentId)
                || documentId == Guid.Empty)
            {
                throw CorruptDraft("Base snapshot document ID is invalid.");
            }
            var revision = RequireInt32(root, "revision");
            if (revision < 1)
            {
                throw CorruptDraft("Base snapshot revision is invalid.");
            }
            return new DocumentDraftSnapshot(
                documentId,
                revision,
                RequireString(root, "title", allowEmpty: false),
                RequireString(root, "virtualPath", allowEmpty: false),
                ParseFormat(RequireString(root, "format", allowEmpty: false)),
                RequireString(root, "content", allowEmpty: true),
                OptionalStringArray(root, "tags"));
        }
        catch (JsonException exception)
        {
            throw CorruptDraft(
                "Base snapshot JSON is invalid.",
                exception);
        }
        catch (VaultException exception)
            when (exception.Code == VaultErrorCode.DraftOperationInvalid)
        {
            throw CorruptDraft(
                "Base snapshot payload is invalid.",
                exception);
        }
    }

    public static CreateDocumentRequest ToCreateRequest(
        DocumentDraftWorkingCopy workingCopy) =>
        new(
            workingCopy.Title,
            workingCopy.VirtualPath,
            null,
            workingCopy.Format,
            workingCopy.Content,
            workingCopy.Tags);

    public static UpdateDocumentRequest ToUpdateRequest(
        DocumentDraftWorkingCopy workingCopy) =>
        new(
            workingCopy.Title,
            workingCopy.VirtualPath,
            workingCopy.Content,
            workingCopy.Tags);

    public static string FormatToDatabaseValue(VaultDocumentFormat format) =>
        format switch
        {
            VaultDocumentFormat.Markdown => "md",
            VaultDocumentFormat.Text => "txt",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

    public static VaultDocumentFormat FormatFromDatabaseValue(string value) =>
        ParseFormat(value);

    private static DocumentDraftPreviewComputation ComputeCreate(
        string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var working = ParseCreatePayload(document.RootElement);
        ValidateContent(working);
        var diff = DocumentPatchEngine.BuildUnifiedDiff(
            string.Empty,
            working.Content);
        return new DocumentDraftPreviewComputation(
            working,
            diff.Text,
            diff.Truncated,
            OperationCount: 0,
            EmptyOperationSummary());
    }

    private static DocumentDraftPreviewComputation ComputeUpdate(
        JsonElement payload,
        DocumentDraftSnapshot baseSnapshot)
    {
        var operations = ParseOperations(payload);
        var operationSummary = SummarizeOperations(operations);
        var working = new DocumentDraftWorkingCopy(
            baseSnapshot.DocumentId,
            baseSnapshot.Title,
            baseSnapshot.VirtualPath,
            baseSnapshot.Format,
            baseSnapshot.Content,
            baseSnapshot.Tags);
        foreach (var operation in operations)
        {
            working = ApplyOperation(working, operation);
            ValidateContent(working);
        }
        if (working.Format == VaultDocumentFormat.Markdown)
        {
            working = working with
            {
                Content = DocumentIdentity.EnsureMarkdownIdentity(
                    working.Content,
                    baseSnapshot.DocumentId,
                    DocumentIdentityMode.ExistingDocument)
            };
        }

        var diff = DocumentPatchEngine.BuildUnifiedDiff(
            baseSnapshot.Content,
            working.Content);
        return new DocumentDraftPreviewComputation(
            working,
            diff.Text,
            diff.Truncated,
            operations.Count,
            operationSummary);
    }

    private static DocumentDraftPreviewComputation ComputeTrash(
        JsonElement payload,
        DocumentDraftSnapshot baseSnapshot)
    {
        ValidateTrashPayload(payload);
        return new DocumentDraftPreviewComputation(
            new DocumentDraftWorkingCopy(
                baseSnapshot.DocumentId,
                baseSnapshot.Title,
                baseSnapshot.VirtualPath,
                baseSnapshot.Format,
                baseSnapshot.Content,
                baseSnapshot.Tags),
            string.Empty,
            DiffTruncated: false,
            OperationCount: 0,
            EmptyOperationSummary());
    }

    public static DocumentDraftOperationSummary GetOperationSummary(
        DocumentDraftType draftType,
        JsonElement payload) =>
        draftType == DocumentDraftType.Update
            ? SummarizeOperations(ParseOperations(payload))
            : EmptyOperationSummary();

    public static DocumentDraftBodyEditCapability GetBodyEditCapability(
        DocumentDraftRecord draft,
        JsonElement payload,
        DocumentDraftSnapshot? snapshot,
        DocumentDraftWorkingCopy preview)
    {
        if (draft.Status is not (
            DocumentDraftStatus.Draft
            or DocumentDraftStatus.PendingReview))
        {
            return new DocumentDraftBodyEditCapability(
                false,
                "draft_not_editable");
        }
        if (draft.DraftType == DocumentDraftType.Create)
        {
            return new DocumentDraftBodyEditCapability(true, null);
        }
        if (draft.DraftType == DocumentDraftType.Trash)
        {
            return new DocumentDraftBodyEditCapability(
                false,
                "trash_not_editable");
        }
        if (snapshot is null
            || !payload.TryGetProperty("operations", out var operations)
            || operations.ValueKind != JsonValueKind.Array)
        {
            return new DocumentDraftBodyEditCapability(
                false,
                "mixed_body_operation_unsafe");
        }

        var candidateOperations = new JsonArray();
        foreach (var operation in operations.EnumerateArray())
        {
            var type = operation.GetProperty("type").GetString();
            if (type is "replace_body" or "append_body")
            {
                continue;
            }
            candidateOperations.Add(JsonNode.Parse(operation.GetRawText()));
        }
        candidateOperations.Add(new JsonObject
        {
            ["type"] = "replace_body",
            ["content"] = preview.Content
        });
        var candidate = new JsonObject
        {
            ["operations"] = candidateOperations
        };
        if (payload.TryGetProperty("reviewContext", out var context)
            && context.ValueKind != JsonValueKind.Null)
        {
            candidate["reviewContext"] = JsonNode.Parse(context.GetRawText());
        }

        try
        {
            _ = ComputeUpdate(
                JsonSerializer.SerializeToElement(candidate, JsonOptions),
                snapshot);
            return new DocumentDraftBodyEditCapability(true, null);
        }
        catch (VaultException)
        {
            return new DocumentDraftBodyEditCapability(
                false,
                "mixed_body_operation_unsafe");
        }
    }

    private static DocumentDraftOperationSummary EmptyOperationSummary() =>
        new(0, []);

    private static DocumentDraftOperationSummary SummarizeOperations(
        IReadOnlyList<DraftOperation> operations)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["replace_body"] = 0,
            ["append_body"] = 0,
            ["set_property"] = 0,
            ["remove_property"] = 0,
            ["add_tag"] = 0,
            ["remove_tag"] = 0
        };
        foreach (var operation in operations)
        {
            counts[OperationType(operation)]++;
        }
        return new DocumentDraftOperationSummary(
            operations.Count,
            OperationTypeOrder
                .Where(type => counts[type] > 0)
                .Select(type => new DocumentDraftOperationCount(
                    type,
                    counts[type]))
                .ToArray());
    }

    private static string OperationType(DraftOperation operation) =>
        operation switch
        {
            ReplaceBodyOperation => "replace_body",
            AppendBodyOperation => "append_body",
            SetPropertyOperation => "set_property",
            RemovePropertyOperation => "remove_property",
            AddTagOperation => "add_tag",
            RemoveTagOperation => "remove_tag",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static IReadOnlyList<DraftOperation> ParseOperations(
        JsonElement payload)
    {
        RequireObject(payload, "Update draft payload");
        DocumentDraftReviewContextCodec.EnsureOnlyProperties(
            payload,
            "Update draft payload",
            UpdatePayloadKeys);
        DocumentDraftReviewContextCodec.ValidateFromPayload(payload);
        if (!payload.TryGetProperty("operations", out var operationsElement)
            || operationsElement.ValueKind != JsonValueKind.Array)
        {
            throw InvalidOperation(
                "Update draft payload must contain an operations array.");
        }

        var operations = new List<DraftOperation>();
        foreach (var item in operationsElement.EnumerateArray())
        {
            if (operations.Count >= MaxOperations)
            {
                throw InvalidOperation(
                    $"A draft can contain at most {MaxOperations} operations.");
            }
            operations.Add(ParseOperation(item));
        }
        if (operations.Count == 0)
        {
            throw InvalidOperation(
                "Update draft payload must contain at least one operation.");
        }
        return operations;
    }

    private static DraftOperation ParseOperation(JsonElement element)
    {
        RequireObject(element, "Draft operation");
        var type = RequireString(element, "type", allowEmpty: false);
        var allowedKeys = type switch
        {
            "replace_body" => ReplaceBodyKeys,
            "append_body" => AppendBodyKeys,
            "set_property" => SetPropertyKeys,
            "remove_property" => RemovePropertyKeys,
            "add_tag" => AddTagKeys,
            "remove_tag" => RemoveTagKeys,
            _ => throw InvalidOperation("Draft operation type is invalid.")
        };
        DocumentDraftReviewContextCodec.EnsureOnlyProperties(
            element,
            "Draft operation",
            allowedKeys);
        return type switch
        {
            "replace_body" => new ReplaceBodyOperation(
                RequireString(element, "content", allowEmpty: true)),
            "append_body" => new AppendBodyOperation(
                RequireString(element, "content", allowEmpty: true)),
            "set_property" => new SetPropertyOperation(
                RequirePropertyKey(element),
                RequireSupportedPropertyValue(element)),
            "remove_property" => new RemovePropertyOperation(
                RequirePropertyKey(element)),
            "add_tag" => new AddTagOperation(
                RequireString(element, "tag", allowEmpty: false)),
            "remove_tag" => new RemoveTagOperation(
                RequireString(element, "tag", allowEmpty: false)),
            _ => throw InvalidOperation("Draft operation type is invalid.")
        };
    }

    private static DocumentDraftWorkingCopy ApplyOperation(
        DocumentDraftWorkingCopy working,
        DraftOperation operation) =>
        operation switch
        {
            ReplaceBodyOperation replace => RefreshContent(
                working with { Content = replace.Content }),
            AppendBodyOperation append => ApplyAppend(working, append.Content),
            SetPropertyOperation set => RefreshContent(
                working with
                {
                    Content = SetProperty(
                        working,
                        set.Key,
                        set.Value,
                        allowReservedKey: false)
                }),
            RemovePropertyOperation remove => RefreshContent(
                working with
                {
                    Content = RemoveProperty(working, remove.Key)
                }),
            AddTagOperation add => ApplyAddTag(working, add.Tag),
            RemoveTagOperation remove => ApplyRemoveTag(working, remove.Tag),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static DocumentDraftWorkingCopy ApplyAppend(
        DocumentDraftWorkingCopy working,
        string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxAppendBytes)
        {
            throw PayloadTooLarge();
        }
        return RefreshContent(
            working with
            {
                Content = working.Content + content
            });
    }

    private static DocumentDraftWorkingCopy ApplyAddTag(
        DocumentDraftWorkingCopy working,
        string tag)
    {
        var normalized = RequireSingleTag(tag);
        var keys = TagNormalizer.NormalizeMany(working.Tags)
            .Select(item => item.NormalizedKey)
            .ToHashSet(StringComparer.Ordinal);
        if (!keys.Add(normalized.NormalizedKey))
        {
            throw InvalidOperation("The tag already exists.");
        }

        if (working.Format == VaultDocumentFormat.Text)
        {
            return working with
            {
                Tags = [.. working.Tags, normalized.Name]
            };
        }

        var tags = working.Tags
            .Append(normalized.Name)
            .ToArray();
        return RefreshContent(
            working with
            {
                Content = SetProperty(
                    working,
                    "tags",
                    JsonSerializer.SerializeToElement(tags),
                    allowReservedKey: true)
            });
    }

    private static DocumentDraftWorkingCopy ApplyRemoveTag(
        DocumentDraftWorkingCopy working,
        string tag)
    {
        var normalized = RequireSingleTag(tag);
        var current = TagNormalizer.NormalizeMany(working.Tags)
            .ToArray();
        var remaining = current
            .Where(item => !string.Equals(
                item.NormalizedKey,
                normalized.NormalizedKey,
                StringComparison.Ordinal))
            .Select(item => item.Name)
            .ToArray();
        if (remaining.Length == current.Length)
        {
            throw InvalidOperation("The tag does not exist.");
        }

        if (working.Format == VaultDocumentFormat.Text)
        {
            return working with { Tags = remaining };
        }

        return RefreshContent(
            working with
            {
                Content = SetProperty(
                    working,
                    "tags",
                    JsonSerializer.SerializeToElement(remaining),
                    allowReservedKey: true)
            });
    }

    private static DocumentDraftWorkingCopy RefreshContent(
        DocumentDraftWorkingCopy working)
    {
        var explicitTags =
            working.Format == VaultDocumentFormat.Markdown
            && !FrontMatterTags.TryExtract(working.Content, out _)
                ? working.Tags
                : null;
        var parsed = DocumentContentParser.Parse(
            working.Title,
            working.VirtualPath,
            null,
            working.Format,
            working.Content,
            explicitTags);
        return working with
        {
            Title = parsed.Title,
            VirtualPath = parsed.Path.Path,
            Content = parsed.Content,
            Tags = parsed.Tags.Select(tag => tag.Name).ToArray()
        };
    }

    private static string SetProperty(
        DocumentDraftWorkingCopy working,
        string key,
        JsonElement value,
        bool allowReservedKey)
    {
        if (working.Format != VaultDocumentFormat.Markdown)
        {
            throw InvalidOperation(
                "Frontmatter properties are available only for Markdown documents.");
        }
        if (!allowReservedKey
            && (string.Equals(key, "id", StringComparison.Ordinal)
                || string.Equals(key, "tags", StringComparison.Ordinal)))
        {
            throw string.Equals(key, "id", StringComparison.Ordinal)
                ? new VaultException(
                    VaultErrorCode.DocumentIdProtected,
                    "The document identity cannot be changed by set_property.")
                : InvalidOperation(
                    "The tags property cannot be changed by set_property.");
        }
        ValidatePropertyValue(value);

        if (!FrontMatterDocument.TryParse(working.Content, out var snapshot))
        {
            var rendered = FrontMatterDocument.RenderField(
                new FrontMatterField(key, string.Empty, value, 0, 0),
                value);
            return $"---\n{rendered}\n---\n{working.Content}";
        }

        var field = snapshot.Fields.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, key, StringComparison.Ordinal));
        if (field is null)
        {
            var rendered = FrontMatterDocument.RenderField(
                new FrontMatterField(key, string.Empty, value, 0, 0),
                value);
            return string.Concat(
                working.Content.AsSpan(0, snapshot.ClosingStart),
                rendered,
                "\n",
                working.Content.AsSpan(snapshot.ClosingStart));
        }

        var computation = DocumentPatchEngine.Apply(
            working.Content,
            new VaultPatchCommand(
                "frontmatter",
                null,
                key,
                "replace",
                null,
                value,
                DryRun: false));
        return computation.Content;
    }

    private static string RemoveProperty(
        DocumentDraftWorkingCopy working,
        string key)
    {
        if (working.Format != VaultDocumentFormat.Markdown)
        {
            throw InvalidOperation(
                "Frontmatter properties are available only for Markdown documents.");
        }
        if (string.Equals(key, "id", StringComparison.Ordinal)
            || string.Equals(key, "tags", StringComparison.Ordinal))
        {
            throw string.Equals(key, "id", StringComparison.Ordinal)
                ? new VaultException(
                    VaultErrorCode.DocumentIdProtected,
                    "The document identity cannot be removed by remove_property.")
                : InvalidOperation(
                    "The tags property cannot be removed by remove_property.");
        }
        if (!FrontMatterDocument.TryParse(working.Content, out var snapshot))
        {
            throw InvalidOperation(
                "The document does not contain Frontmatter.");
        }
        if (!snapshot.Fields.Any(field =>
                string.Equals(field.Key, key, StringComparison.Ordinal)))
        {
            throw InvalidOperation(
                "The Frontmatter property does not exist.");
        }
        var computation = DocumentPatchEngine.Apply(
            working.Content,
            new VaultPatchCommand(
                "frontmatter",
                null,
                key,
                "delete",
                null,
                null,
                DryRun: false));
        return computation.Content;
    }

    private static void ValidateContent(DocumentDraftWorkingCopy working)
    {
        if (Encoding.UTF8.GetByteCount(working.Content) > MaxPayloadBytes)
        {
            throw PayloadTooLarge();
        }
    }

    private static JsonElement RequireSupportedPropertyValue(JsonElement operation)
    {
        if (!operation.TryGetProperty("value", out var value))
        {
            throw InvalidOperation("set_property requires a value.");
        }
        ValidatePropertyValue(value);
        return value.Clone();
    }

    private static void ValidatePropertyValue(JsonElement value)
    {
        if (value.ValueKind is
            JsonValueKind.String
            or JsonValueKind.Number
            or JsonValueKind.True
            or JsonValueKind.False
            or JsonValueKind.Null)
        {
            return;
        }
        if (value.ValueKind == JsonValueKind.Array
            && value.EnumerateArray().All(item =>
                item.ValueKind is
                    JsonValueKind.String
                    or JsonValueKind.Number
                    or JsonValueKind.True
                    or JsonValueKind.False
                    or JsonValueKind.Null))
        {
            return;
        }
        throw InvalidOperation(
            "Frontmatter values support only scalars or flat scalar arrays.");
    }

    private static string RequirePropertyKey(JsonElement element)
    {
        var key = RequireString(element, "key", allowEmpty: false);
        if (!PropertyKeyPattern.IsMatch(key))
        {
            throw InvalidOperation("Frontmatter property key is invalid.");
        }
        return key;
    }

    private static NormalizedTag RequireSingleTag(string value)
    {
        var tags = TagNormalizer.NormalizeMany([value]);
        return tags.Count == 1
            ? tags[0]
            : throw InvalidOperation("Tag is invalid.");
    }

    private static string RequireString(
        JsonElement element,
        string name,
        bool allowEmpty)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw InvalidOperation($"{name} must be a string.");
        }
        var text = value.GetString() ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(text))
        {
            throw InvalidOperation($"{name} must not be empty.");
        }
        return text;
    }

    private static string? OptionalString(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidOperation($"{name} must be a string or null.");
        }
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static IReadOnlyList<string> OptionalStringArray(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (value.ValueKind != JsonValueKind.Array
            || value.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.String))
        {
            throw InvalidOperation($"{name} must be a string array.");
        }
        return value.EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
    }

    private static int RequireInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result))
        {
            throw CorruptDraft($"{name} must be an integer.");
        }
        return result;
    }

    private static void RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidOperation($"{name} must be a JSON object.");
        }
    }

    private static HashSet<string> OperationKeys(
        string type,
        params string[] fields)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal)
        {
            "type"
        };
        _ = type;
        foreach (var field in fields)
        {
            _ = keys.Add(field);
        }
        return keys;
    }

    private static VaultDocumentFormat ParseFormat(string value) =>
        value switch
        {
            "md" => VaultDocumentFormat.Markdown,
            "txt" => VaultDocumentFormat.Text,
            _ => throw InvalidOperation("format must be md or txt.")
        };

    private static VaultException InvalidOperation(string message) =>
        new(VaultErrorCode.DraftOperationInvalid, message);

    private static VaultException PayloadTooLarge() =>
        new(
            VaultErrorCode.DraftPayloadTooLarge,
            "The draft payload exceeds the allowed size.");

    private static VaultException CorruptDraft(
        string message,
        Exception? innerException = null) =>
        new(
            VaultErrorCode.CorruptVault,
            message,
            innerException);

    private abstract record DraftOperation;

    private sealed record ReplaceBodyOperation(string Content) : DraftOperation;

    private sealed record AppendBodyOperation(string Content) : DraftOperation;

    private sealed record SetPropertyOperation(
        string Key,
        JsonElement Value) : DraftOperation;

    private sealed record RemovePropertyOperation(string Key) : DraftOperation;

    private sealed record AddTagOperation(string Tag) : DraftOperation;

    private sealed record RemoveTagOperation(string Tag) : DraftOperation;
}

internal sealed record PreparedCreateDraftPayload(
    Guid FutureDocumentId,
    JsonElement PersistedPayload,
    DocumentDraftWorkingCopy WorkingCopy);
