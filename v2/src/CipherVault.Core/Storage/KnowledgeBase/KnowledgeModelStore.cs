using System.Globalization;
using System.Text.Json;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Storage.KnowledgeBase;

public static class KnowledgeModelStore
{
    private static readonly HashSet<string> ValueTypes =
    [
        "string",
        "number",
        "boolean",
        "date",
        "enum",
        "array",
        "object"
    ];
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task<KnowledgeModelSnapshot> GetAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (version, updatedAt) = await ReadStateAsync(
            context,
            cancellationToken);
        var properties = await ReadPropertiesAsync(context, cancellationToken);
        var types = await ReadTypesAsync(
            context,
            properties,
            cancellationToken);
        var templates = await ReadTemplatesAsync(context, cancellationToken);
        var relationTypes = await ReadRelationTypesAsync(
            context,
            cancellationToken);
        var sourceRules = await ReadSourceRulesAsync(
            context,
            cancellationToken);
        var workflows = await ReadWorkflowsAsync(context, cancellationToken);
        return new KnowledgeModelSnapshot(
            version,
            updatedAt,
            types,
            templates,
            relationTypes,
            sourceRules,
            workflows);
    }

    public static async Task<KnowledgeModelSnapshot> ReplaceAsync(
        VaultOperationContext context,
        int expectedVersion,
        KnowledgeModelUpdate update,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(update);
        ValidateKeys(update);

        var state = await ReadStateAsync(context, cancellationToken);
        if (state.Version != expectedVersion)
        {
            throw new VaultException(
                VaultErrorCode.PreconditionFailed,
                "Knowledge Model version changed.");
        }

        var normalizedNow = now.ToUniversalTime();
        var updatedAt = normalizedNow >= state.UpdatedAt
            ? normalizedNow
            : state.UpdatedAt;
        var nowText = updatedAt.ToString("O");
        var assignedTypes = update.KnowledgeTypes
            .Select(type => type with
            {
                TypeId = type.TypeId ?? Guid.NewGuid(),
                Properties = type.Properties
                    .Select(property => property with
                    {
                        PropertyId = property.PropertyId ?? Guid.NewGuid()
                    })
                    .ToArray()
            })
            .ToArray();
        var assignedTemplates = update.Templates
            .Select(template => template with
            {
                TemplateId = template.TemplateId ?? Guid.NewGuid()
            })
            .ToArray();
        var assignedRelations = update.RelationTypes
            .Select(relation => relation with
            {
                RelationTypeId = relation.RelationTypeId ?? Guid.NewGuid()
            })
            .ToArray();
        var existingRules = await ReadSourceRuleIdsAsync(
            context,
            cancellationToken);
        var assignedRules = update.SourceRules
            .Select(rule => rule with
            {
                SourceRuleId = rule.SourceRuleId
                    ?? (existingRules.TryGetValue(rule.Kind, out var existingId)
                        ? existingId
                        : Guid.NewGuid())
            })
            .ToArray();
        var assignedWorkflows = update.Workflows
            .Select(workflow => workflow with
            {
                WorkflowId = workflow.WorkflowId ?? Guid.NewGuid()
            })
            .ToArray();

        using (var bump = context.CreateCommand())
        {
            bump.Command.CommandText =
                """
                UPDATE knowledge_model_state
                SET version = version + 1, updated_at = $updatedAt
                WHERE id = 1 AND version = $expectedVersion;
                """;
            bump.Command.Parameters.AddWithValue(
                "$updatedAt",
                nowText);
            bump.Command.Parameters.AddWithValue(
                "$expectedVersion",
                expectedVersion);
            var affected = await bump.Command.ExecuteNonQueryAsync(
                cancellationToken);
            if (affected != 1)
            {
                throw new VaultException(
                    VaultErrorCode.PreconditionFailed,
                    "Knowledge Model version changed.");
            }
        }

        await DisableAllAsync(context, nowText, cancellationToken);
        foreach (var type in assignedTypes)
        {
            await UpsertTypeAsync(context, type, nowText, cancellationToken);
            foreach (var property in type.Properties)
            {
                await UpsertPropertyAsync(
                    context,
                    type.TypeId!.Value,
                    property,
                    nowText,
                    cancellationToken);
            }
        }
        foreach (var template in assignedTemplates)
        {
            await UpsertTemplateAsync(
                context,
                template,
                nowText,
                cancellationToken);
        }
        foreach (var relation in assignedRelations)
        {
            await UpsertRelationTypeAsync(
                context,
                relation,
                nowText,
                cancellationToken);
        }
        foreach (var rule in assignedRules)
        {
            await UpsertSourceRuleAsync(
                context,
                rule,
                nowText,
                cancellationToken);
        }
        foreach (var workflow in assignedWorkflows)
        {
            await UpsertWorkflowAsync(
                context,
                workflow,
                nowText,
                cancellationToken);
        }

        await InsertAuditAsync(
            context,
            expectedVersion + 1,
            updatedAt,
            cancellationToken);
        return await GetAsync(context, cancellationToken);
    }

    private static void ValidateKeys(KnowledgeModelUpdate update)
    {
        EnsureUnique(update.KnowledgeTypes.Select(item => item.Key));
        EnsureUnique(update.RelationTypes.Select(item => item.Key));
        EnsureUnique(update.Templates.Select(item => item.Key));
        EnsureUnique(update.SourceRules.Select(item => item.Kind));
        EnsureUnique(update.Workflows.Select(item => item.Key));
        foreach (var type in update.KnowledgeTypes)
        {
            EnsureUnique(type.Properties.Select(item => item.Key));
        }
        ValidateDefinitions(update);
    }

    private static void ValidateDefinitions(KnowledgeModelUpdate update)
    {
        foreach (var type in update.KnowledgeTypes)
        {
            foreach (var property in type.Properties)
            {
                ValidatePropertyDefinition(
                    property,
                    $"knowledgeTypes.{type.Key}.properties.{property.Key}");
            }
        }
        foreach (var rule in update.SourceRules)
        {
            ValidateSourceSchema(
                rule.MetadataSchema,
                $"sourceRules.{rule.Kind.ToDatabaseValue()}");
        }
    }

    private static void ValidatePropertyDefinition(
        KnowledgePropertyUpdate property,
        string fieldPath)
    {
        if (!ValueTypes.Contains(property.ValueType))
        {
            throw ModelInvalid(
                $"{fieldPath}.valueType",
                "Property value type is invalid.");
        }
        if (property.Constraints.ValueKind != JsonValueKind.Object)
        {
            throw ModelInvalid(
                $"{fieldPath}.constraints",
                "Property constraints must be an object.");
        }
        ValidateConstraintShape(
            property.ValueType,
            property.Constraints,
            $"{fieldPath}.constraints");
        if (property.DefaultValue is { } defaultValue
            && !JsonValueMatches(
                defaultValue,
                property.ValueType,
                property.Constraints))
        {
            throw ModelInvalid(
                $"{fieldPath}.defaultValue",
                "Property default value does not match its definition.");
        }
    }

    private static void ValidateConstraintShape(
        string valueType,
        JsonElement constraints,
        string fieldPath)
    {
        ValidateOptionalBoolean(
            constraints,
            "integer",
            fieldPath);
        ValidateOptionalNumber(
            constraints,
            "minimum",
            fieldPath);
        ValidateOptionalNumber(
            constraints,
            "maximum",
            fieldPath);
        ValidateNumberBounds(constraints, fieldPath);
        ValidateOptionalNonNegativeInteger(
            constraints,
            "minLength",
            fieldPath);
        ValidateOptionalNonNegativeInteger(
            constraints,
            "maxLength",
            fieldPath);
        ValidateIntegerBounds(
            constraints,
            "minLength",
            "maxLength",
            fieldPath);
        ValidateOptionalNonNegativeInteger(
            constraints,
            "minItems",
            fieldPath);
        ValidateOptionalNonNegativeInteger(
            constraints,
            "maxItems",
            fieldPath);
        ValidateIntegerBounds(
            constraints,
            "minItems",
            "maxItems",
            fieldPath);
        if (constraints.TryGetProperty("options", out var options)
            && (options.ValueKind != JsonValueKind.Array
                || options.GetArrayLength() == 0
                || options.EnumerateArray().Any(item =>
                    item.ValueKind != JsonValueKind.String)))
        {
            throw ModelInvalid(
                $"{fieldPath}.options",
                "Enum options must be a non-empty string array.");
        }
        if (valueType == "enum"
            && !constraints.TryGetProperty("options", out _))
        {
            throw ModelInvalid(
                $"{fieldPath}.options",
                "Enum properties require options.");
        }
        if (constraints.TryGetProperty("items", out var items))
        {
            ValidateSchemaShape(
                items,
                $"{fieldPath}.items");
        }
        if (constraints.TryGetProperty("properties", out var properties)
            && properties.ValueKind != JsonValueKind.Object)
        {
            throw ModelInvalid(
                $"{fieldPath}.properties",
                "Object properties must be an object.");
        }
        if (constraints.TryGetProperty(
                "additionalProperties",
                out var additional)
            && additional.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            throw ModelInvalid(
                $"{fieldPath}.additionalProperties",
                "additionalProperties must be boolean.");
        }
    }

    private static void ValidateSourceSchema(
        JsonElement schema,
        string fieldPath)
    {
        ValidateSchemaShape(schema, fieldPath);
        if (!schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            throw ModelInvalid(
                $"{fieldPath}.properties",
                "Source metadata schema requires object properties.");
        }
        if (schema.TryGetProperty("required", out var required))
        {
            if (required.ValueKind != JsonValueKind.Array
                || required.EnumerateArray().Any(item =>
                    item.ValueKind != JsonValueKind.String))
            {
                throw ModelInvalid(
                    $"{fieldPath}.required",
                    "Source metadata required must be a string array.");
            }
            var names = properties.EnumerateObject()
                .Select(item => item.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var item in required.EnumerateArray())
            {
                var name = item.GetString() ?? string.Empty;
                if (!names.Contains(name))
                {
                    throw ModelInvalid(
                        $"{fieldPath}.required",
                        "Required source metadata field is not defined.");
                }
            }
        }
    }

    private static void ValidateSchemaShape(
        JsonElement schema,
        string fieldPath)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw ModelInvalid(fieldPath, "Schema must be an object.");
        }
        if (schema.TryGetProperty("type", out var type)
            && (type.ValueKind != JsonValueKind.String
                || !ValueTypes.Contains(type.GetString() ?? string.Empty)))
        {
            throw ModelInvalid(
                $"{fieldPath}.type",
                "Schema type is invalid.");
        }
        if (schema.TryGetProperty("itemType", out var itemType)
            && (itemType.ValueKind != JsonValueKind.String
                || !ValueTypes.Contains(
                    itemType.GetString() ?? string.Empty)))
        {
            throw ModelInvalid(
                $"{fieldPath}.itemType",
                "Schema item type is invalid.");
        }
        if (string.Equals(
                type.ValueKind == JsonValueKind.String
                    ? type.GetString()
                    : null,
                "enum",
                StringComparison.Ordinal)
            && (!schema.TryGetProperty("options", out var options)
                || options.ValueKind != JsonValueKind.Array
                || options.GetArrayLength() == 0
                || options.EnumerateArray().Any(item =>
                    item.ValueKind != JsonValueKind.String)))
        {
            throw ModelInvalid(
                $"{fieldPath}.options",
                "Enum schema options must be a non-empty string array.");
        }
        if (schema.TryGetProperty("properties", out var properties))
        {
            if (properties.ValueKind != JsonValueKind.Object)
            {
                throw ModelInvalid(
                    $"{fieldPath}.properties",
                    "Schema properties must be an object.");
            }
            foreach (var property in properties.EnumerateObject())
            {
                ValidateSchemaShape(
                    property.Value,
                    $"{fieldPath}.properties.{property.Name}");
            }
        }
        if (schema.TryGetProperty(
                "additionalProperties",
                out var additional)
            && additional.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            throw ModelInvalid(
                $"{fieldPath}.additionalProperties",
                "additionalProperties must be boolean.");
        }
    }

    private static void ValidateOptionalBoolean(
        JsonElement value,
        string name,
        string fieldPath)
    {
        if (value.TryGetProperty(name, out var property)
            && property.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            throw ModelInvalid(
                $"{fieldPath}.{name}",
                $"{name} must be boolean.");
        }
    }

    private static void ValidateOptionalNumber(
        JsonElement value,
        string name,
        string fieldPath)
    {
        if (value.TryGetProperty(name, out var property)
            && property.ValueKind != JsonValueKind.Number)
        {
            throw ModelInvalid(
                $"{fieldPath}.{name}",
                $"{name} must be a number.");
        }
    }

    private static void ValidateOptionalNonNegativeInteger(
        JsonElement value,
        string name,
        string fieldPath)
    {
        if (value.TryGetProperty(name, out var property)
            && (property.ValueKind != JsonValueKind.Number
                || !property.TryGetInt32(out var number)
                || number < 0))
        {
            throw ModelInvalid(
                $"{fieldPath}.{name}",
                $"{name} must be a non-negative integer.");
        }
    }

    private static void ValidateNumberBounds(
        JsonElement constraints,
        string fieldPath)
    {
        if (constraints.TryGetProperty("minimum", out var minimum)
            && constraints.TryGetProperty("maximum", out var maximum)
            && minimum.GetDecimal() > maximum.GetDecimal())
        {
            throw ModelInvalid(
                $"{fieldPath}.maximum",
                "maximum must not be below minimum.");
        }
    }

    private static void ValidateIntegerBounds(
        JsonElement constraints,
        string minimumName,
        string maximumName,
        string fieldPath)
    {
        if (constraints.TryGetProperty(minimumName, out var minimum)
            && constraints.TryGetProperty(maximumName, out var maximum)
            && minimum.GetInt32() > maximum.GetInt32())
        {
            throw ModelInvalid(
                $"{fieldPath}.{maximumName}",
                $"{maximumName} must not be below {minimumName}.");
        }
    }

    private static bool JsonValueMatches(
        JsonElement value,
        string valueType,
        JsonElement constraints)
    {
        return valueType switch
        {
            "string" or "date" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is
                JsonValueKind.True or JsonValueKind.False,
            "enum" => value.ValueKind == JsonValueKind.String
                && constraints.TryGetProperty("options", out var options)
                && options.EnumerateArray().Any(item =>
                    string.Equals(
                        item.GetString(),
                        value.GetString(),
                        StringComparison.Ordinal)),
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            _ => false
        };
    }

    private static VaultException ModelInvalid(
        string fieldPath,
        string message) =>
        new(
            VaultErrorCode.KnowledgeModelInvalid,
            message,
            details: new Dictionary<string, object?>
            {
                ["fieldPath"] = fieldPath
            });

    private static void EnsureUnique<T>(IEnumerable<T> values)
    {
        var array = values.ToArray();
        if (array.Distinct().Count() != array.Length)
        {
            throw new VaultException(
                VaultErrorCode.KnowledgeModelInvalid,
                "Knowledge Model contains duplicate keys.");
        }
    }

    private static async Task DisableAllAsync(
        VaultOperationContext context,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            UPDATE knowledge_types
            SET is_enabled = 0, updated_at = $updatedAt
            WHERE is_enabled = 1;
            UPDATE knowledge_property_definitions
            SET is_enabled = 0, updated_at = $updatedAt
            WHERE is_enabled = 1;
            UPDATE knowledge_templates
            SET is_enabled = 0, updated_at = $updatedAt
            WHERE is_enabled = 1;
            UPDATE relation_types
            SET is_enabled = 0, updated_at = $updatedAt
            WHERE is_enabled = 1;
            UPDATE source_rules
            SET is_enabled = 0, updated_at = $updatedAt
            WHERE is_enabled = 1;
            UPDATE workflow_definitions
            SET is_enabled = 0, updated_at = $updatedAt
            WHERE is_enabled = 1;
            """;
        command.Command.Parameters.AddWithValue("$updatedAt", updatedAt);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertTypeAsync(
        VaultOperationContext context,
        KnowledgeTypeUpdate type,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO knowledge_types(
                type_id, type_key, name, description, is_enabled, sort_order,
                created_at, updated_at)
            VALUES(
                $id, $key, $name, $description, $enabled, $sortOrder,
                $updatedAt, $updatedAt)
            ON CONFLICT(type_id) DO UPDATE SET
                name = excluded.name,
                description = excluded.description,
                is_enabled = excluded.is_enabled,
                sort_order = excluded.sort_order,
                updated_at = excluded.updated_at;
            """;
        command.Command.Parameters.AddWithValue(
            "$id",
            type.TypeId!.Value.ToString("D"));
        command.Command.Parameters.AddWithValue("$key", type.Key);
        command.Command.Parameters.AddWithValue("$name", type.Name);
        command.Command.Parameters.AddWithValue(
            "$description",
            type.Description ?? (object)DBNull.Value);
        command.Command.Parameters.AddWithValue(
            "$enabled",
            type.IsEnabled ? 1 : 0);
        command.Command.Parameters.AddWithValue("$sortOrder", type.SortOrder);
        command.Command.Parameters.AddWithValue("$updatedAt", updatedAt);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertPropertyAsync(
        VaultOperationContext context,
        Guid typeId,
        KnowledgePropertyUpdate property,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO knowledge_property_definitions(
                property_id, knowledge_type_id, property_key, name, value_type,
                is_required, default_value_json, constraints_json, is_enabled,
                sort_order, created_at, updated_at)
            VALUES(
                $id, $typeId, $key, $name, $valueType, $required,
                $defaultValue, $constraints, $enabled, $sortOrder,
                $updatedAt, $updatedAt)
            ON CONFLICT(property_id) DO UPDATE SET
                name = excluded.name,
                value_type = excluded.value_type,
                is_required = excluded.is_required,
                default_value_json = excluded.default_value_json,
                constraints_json = excluded.constraints_json,
                is_enabled = excluded.is_enabled,
                sort_order = excluded.sort_order,
                updated_at = excluded.updated_at;
            """;
        command.Command.Parameters.AddWithValue(
            "$id",
            property.PropertyId!.Value.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$typeId",
            typeId.ToString("D"));
        command.Command.Parameters.AddWithValue("$key", property.Key);
        command.Command.Parameters.AddWithValue("$name", property.Name);
        command.Command.Parameters.AddWithValue(
            "$valueType",
            property.ValueType);
        command.Command.Parameters.AddWithValue(
            "$required",
            property.Required ? 1 : 0);
        command.Command.Parameters.AddWithValue(
            "$defaultValue",
            property.DefaultValue is { } value
                ? value.GetRawText()
                : DBNull.Value);
        command.Command.Parameters.AddWithValue(
            "$constraints",
            property.Constraints.GetRawText());
        command.Command.Parameters.AddWithValue(
            "$enabled",
            property.IsEnabled ? 1 : 0);
        command.Command.Parameters.AddWithValue(
            "$sortOrder",
            property.SortOrder);
        command.Command.Parameters.AddWithValue("$updatedAt", updatedAt);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertTemplateAsync(
        VaultOperationContext context,
        KnowledgeTemplateUpdate template,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO knowledge_templates(
                template_id, knowledge_type_id, template_key, name, description,
                body_template, fields_json, is_enabled, sort_order,
                created_at, updated_at)
            VALUES(
                $id, $typeId, $key, $name, $description, $body, $fields,
                $enabled, $sortOrder, $updatedAt, $updatedAt)
            ON CONFLICT(template_id) DO UPDATE SET
                knowledge_type_id = excluded.knowledge_type_id,
                name = excluded.name,
                description = excluded.description,
                body_template = excluded.body_template,
                fields_json = excluded.fields_json,
                is_enabled = excluded.is_enabled,
                sort_order = excluded.sort_order,
                updated_at = excluded.updated_at;
            """;
        command.Command.Parameters.AddWithValue(
            "$id",
            template.TemplateId!.Value.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$typeId",
            template.KnowledgeTypeId.ToString("D"));
        command.Command.Parameters.AddWithValue("$key", template.Key);
        command.Command.Parameters.AddWithValue("$name", template.Name);
        command.Command.Parameters.AddWithValue(
            "$description",
            template.Description ?? (object)DBNull.Value);
        command.Command.Parameters.AddWithValue("$body", template.BodyTemplate);
        command.Command.Parameters.AddWithValue(
            "$fields",
            template.Fields.GetRawText());
        command.Command.Parameters.AddWithValue(
            "$enabled",
            template.IsEnabled ? 1 : 0);
        command.Command.Parameters.AddWithValue(
            "$sortOrder",
            template.SortOrder);
        command.Command.Parameters.AddWithValue("$updatedAt", updatedAt);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertRelationTypeAsync(
        VaultOperationContext context,
        RelationTypeUpdate relation,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO relation_types(
                relation_type_id, relation_key, name, description, is_directed,
                is_enabled, sort_order, created_at, updated_at)
            VALUES(
                $id, $key, $name, $description, $directed, $enabled,
                $sortOrder, $updatedAt, $updatedAt)
            ON CONFLICT(relation_type_id) DO UPDATE SET
                name = excluded.name,
                description = excluded.description,
                is_enabled = excluded.is_enabled,
                sort_order = excluded.sort_order,
                updated_at = excluded.updated_at;
            """;
        command.Command.Parameters.AddWithValue(
            "$id",
            relation.RelationTypeId!.Value.ToString("D"));
        command.Command.Parameters.AddWithValue("$key", relation.Key);
        command.Command.Parameters.AddWithValue("$name", relation.Name);
        command.Command.Parameters.AddWithValue(
            "$description",
            relation.Description ?? (object)DBNull.Value);
        command.Command.Parameters.AddWithValue(
            "$directed",
            relation.IsDirected ? 1 : 0);
        command.Command.Parameters.AddWithValue(
            "$enabled",
            relation.IsEnabled ? 1 : 0);
        command.Command.Parameters.AddWithValue(
            "$sortOrder",
            relation.SortOrder);
        command.Command.Parameters.AddWithValue("$updatedAt", updatedAt);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertSourceRuleAsync(
        VaultOperationContext context,
        SourceRuleUpdate rule,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO source_rules(
                source_rule_id, source_kind, metadata_schema_json,
                supported_locator_types_json, is_enabled, created_at, updated_at)
            VALUES(
                $id, $kind, $schema, $locators, $enabled, $updatedAt, $updatedAt)
            ON CONFLICT(source_rule_id) DO UPDATE SET
                metadata_schema_json = excluded.metadata_schema_json,
                supported_locator_types_json =
                    excluded.supported_locator_types_json,
                is_enabled = excluded.is_enabled,
                updated_at = excluded.updated_at;
            """;
        command.Command.Parameters.AddWithValue(
            "$id",
            rule.SourceRuleId!.Value.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$kind",
            rule.Kind.ToDatabaseValue());
        command.Command.Parameters.AddWithValue(
            "$schema",
            rule.MetadataSchema.GetRawText());
        command.Command.Parameters.AddWithValue(
            "$locators",
            JsonSerializer.Serialize(
                rule.SupportedLocatorTypes.Select(item =>
                    item.ToDatabaseValue()),
                JsonOptions));
        command.Command.Parameters.AddWithValue(
            "$enabled",
            rule.IsEnabled ? 1 : 0);
        command.Command.Parameters.AddWithValue("$updatedAt", updatedAt);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertWorkflowAsync(
        VaultOperationContext context,
        WorkflowUpdate workflow,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO workflow_definitions(
                workflow_id, workflow_key, name, description,
                target_object_types_json, constraints_json, is_enabled,
                sort_order, created_at, updated_at)
            VALUES(
                $id, $key, $name, $description, $targets, $constraints,
                $enabled, $sortOrder, $updatedAt, $updatedAt)
            ON CONFLICT(workflow_id) DO UPDATE SET
                name = excluded.name,
                description = excluded.description,
                target_object_types_json = excluded.target_object_types_json,
                constraints_json = excluded.constraints_json,
                is_enabled = excluded.is_enabled,
                sort_order = excluded.sort_order,
                updated_at = excluded.updated_at;
            """;
        command.Command.Parameters.AddWithValue(
            "$id",
            workflow.WorkflowId!.Value.ToString("D"));
        command.Command.Parameters.AddWithValue("$key", workflow.Key);
        command.Command.Parameters.AddWithValue("$name", workflow.Name);
        command.Command.Parameters.AddWithValue(
            "$description",
            workflow.Description ?? (object)DBNull.Value);
        command.Command.Parameters.AddWithValue(
            "$targets",
            JsonSerializer.Serialize(
                workflow.TargetObjectTypes.Select(item =>
                    item.ToDatabaseValue()),
                JsonOptions));
        command.Command.Parameters.AddWithValue(
            "$constraints",
            workflow.Constraints.GetRawText());
        command.Command.Parameters.AddWithValue(
            "$enabled",
            workflow.IsEnabled ? 1 : 0);
        command.Command.Parameters.AddWithValue(
            "$sortOrder",
            workflow.SortOrder);
        command.Command.Parameters.AddWithValue("$updatedAt", updatedAt);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<SourceKind, Guid>>
        ReadSourceRuleIdsAsync(
            VaultOperationContext context,
            CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            "SELECT source_kind, source_rule_id FROM source_rules;";
        var result = new Dictionary<SourceKind, Guid>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[KnowledgeBaseValueMapping.ParseSourceKind(
                reader.GetString(0))] = Guid.Parse(reader.GetString(1));
        }
        return result;
    }

    private static async Task InsertAuditAsync(
        VaultOperationContext context,
        int version,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO audit_events(
                event_id, occurred_at, actor_kind, action, target_object_type,
                target_id, target_revision, correlation_id, result, details_json)
            VALUES(
                $eventId, $occurredAt, 'user_ui', 'knowledge_model.change',
                'knowledge_model', NULL, $version, NULL, 'success',
                json_object('version', $version));
            """;
        command.Command.Parameters.AddWithValue(
            "$eventId",
            Guid.NewGuid().ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$occurredAt",
            occurredAt.ToString("O"));
        command.Command.Parameters.AddWithValue("$version", version);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(int Version, DateTimeOffset UpdatedAt)>
        ReadStateAsync(
            VaultOperationContext context,
            CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            "SELECT version, updated_at FROM knowledge_model_state WHERE id = 1;";
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "knowledge_model_state is missing.");
        }
        return (
            reader.GetInt32(0),
            ParseTimestamp(reader.GetString(1)));
    }

    private static async Task<
        IReadOnlyDictionary<Guid, IReadOnlyList<KnowledgePropertyDefinition>>>
        ReadPropertiesAsync(
            VaultOperationContext context,
            CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                property_id,
                knowledge_type_id,
                property_key,
                name,
                value_type,
                is_required,
                default_value_json,
                constraints_json,
                is_enabled,
                sort_order
            FROM knowledge_property_definitions
            ORDER BY knowledge_type_id, sort_order, property_id;
            """;
        var result = new Dictionary<Guid, List<KnowledgePropertyDefinition>>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var typeId = Guid.Parse(reader.GetString(1));
            if (!result.TryGetValue(typeId, out var items))
            {
                items = [];
                result[typeId] = items;
            }
            items.Add(new KnowledgePropertyDefinition(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5) == 1,
                reader.IsDBNull(6)
                    ? null
                    : ParseJson(reader.GetString(6)),
                ParseJson(reader.GetString(7)),
                reader.GetInt32(8) == 1,
                reader.GetInt32(9)));
        }
        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<KnowledgePropertyDefinition>)pair.Value);
    }

    private static async Task<IReadOnlyList<KnowledgeTypeDefinition>>
        ReadTypesAsync(
            VaultOperationContext context,
            IReadOnlyDictionary<Guid, IReadOnlyList<KnowledgePropertyDefinition>>
                properties,
            CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                type_id,
                type_key,
                name,
                description,
                is_enabled,
                sort_order
            FROM knowledge_types
            ORDER BY sort_order, type_id;
            """;
        var result = new List<KnowledgeTypeDefinition>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var typeId = Guid.Parse(reader.GetString(0));
            result.Add(new KnowledgeTypeDefinition(
                typeId,
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4) == 1,
                reader.GetInt32(5),
                properties.TryGetValue(typeId, out var definitions)
                    ? definitions
                    : []));
        }
        return result;
    }

    private static async Task<IReadOnlyList<KnowledgeTemplateDefinition>>
        ReadTemplatesAsync(
            VaultOperationContext context,
            CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                template_id,
                knowledge_type_id,
                template_key,
                name,
                description,
                body_template,
                fields_json,
                is_enabled,
                sort_order
            FROM knowledge_templates
            ORDER BY sort_order, template_id;
            """;
        var result = new List<KnowledgeTemplateDefinition>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new KnowledgeTemplateDefinition(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                ParseJson(reader.GetString(6)),
                reader.GetInt32(7) == 1,
                reader.GetInt32(8)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<RelationTypeDefinition>>
        ReadRelationTypesAsync(
            VaultOperationContext context,
            CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                relation_type_id,
                relation_key,
                name,
                description,
                is_directed,
                is_enabled,
                sort_order
            FROM relation_types
            ORDER BY sort_order, relation_type_id;
            """;
        var result = new List<RelationTypeDefinition>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new RelationTypeDefinition(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4) == 1,
                reader.GetInt32(5) == 1,
                reader.GetInt32(6)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<SourceRuleDefinition>>
        ReadSourceRulesAsync(
            VaultOperationContext context,
            CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                source_rule_id,
                source_kind,
                metadata_schema_json,
                supported_locator_types_json,
                is_enabled
            FROM source_rules
            ORDER BY source_kind;
            """;
        var result = new List<SourceRuleDefinition>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var locatorTypes = ParseJson(reader.GetString(3))
                .EnumerateArray()
                .Select(item => KnowledgeBaseValueMapping.ParseLocatorType(
                    item.GetString() ?? string.Empty))
                .ToArray();
            result.Add(new SourceRuleDefinition(
                Guid.Parse(reader.GetString(0)),
                KnowledgeBaseValueMapping.ParseSourceKind(
                    reader.GetString(1)),
                ParseJson(reader.GetString(2)),
                locatorTypes,
                reader.GetInt32(4) == 1));
        }
        return result;
    }

    private static async Task<IReadOnlyList<WorkflowDefinition>>
        ReadWorkflowsAsync(
            VaultOperationContext context,
            CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                workflow_id,
                workflow_key,
                name,
                description,
                target_object_types_json,
                constraints_json,
                is_enabled,
                sort_order
            FROM workflow_definitions
            ORDER BY sort_order, workflow_id;
            """;
        var result = new List<WorkflowDefinition>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var targetTypes = ParseJson(reader.GetString(4))
                .EnumerateArray()
                .Select(item =>
                    KnowledgeBaseValueMapping.ParseTargetObjectType(
                        item.GetString() ?? string.Empty))
                .ToArray();
            result.Add(new WorkflowDefinition(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                targetTypes,
                ParseJson(reader.GetString(5)),
                reader.GetInt32(6) == 1,
                reader.GetInt32(7)));
        }
        return result;
    }

    private static JsonElement ParseJson(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();
}
