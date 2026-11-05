using CipherVault.Core.KnowledgeBase;
using CipherVault.Core.Storage.KnowledgeBase;
using CipherVault.Application.KnowledgeBase;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class KnowledgeModelStoreTests
{
    [Fact]
    public async Task UnifiedDraftCanBeCreatedAndSubmitted()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var service = new UnifiedDraftService(
            fixture.Manager,
            fixture.TimeProvider);
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var payload = JsonSerializer.SerializeToElement(new
        {
            sourceKind = "web_page",
            title = "Source",
            metadata = new { url = "https://example.com" },
            contentFormat = "markdown",
            contentText = "# Section\n\nParagraph"
        });
        var created = await service.CreateAsync(
            new CreateDraftInput(
                DraftTargetObjectType.Source,
                DraftOperationType.Create,
                null,
                null,
                payload),
            actor,
            Guid.NewGuid());
        var submitted = await service.SubmitAsync(
            created.DraftId,
            created.DraftRevision,
            actor,
            Guid.NewGuid());
        var applied = await service.ApplyAsync(
            submitted.DraftId,
            submitted.DraftRevision,
            actor,
            Guid.NewGuid());

        Assert.Equal(DraftStatus.PendingReview, submitted.Status);
        Assert.Equal(DraftTargetObjectType.Source, applied.ObjectType);
    }

    [Fact]
    public async Task ReplacePersistsModelVersion()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var result = await fixture.Manager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var model = await KnowledgeModelStore.GetAsync(context, token);
                var update = new KnowledgeModelUpdate(
                    model.KnowledgeTypes.Select(type => new KnowledgeTypeUpdate(
                        type.TypeId,
                        type.Key,
                        type.Name,
                        type.Description,
                        type.IsEnabled,
                        type.SortOrder,
                        type.Properties.Select(property =>
                            new KnowledgePropertyUpdate(
                                property.PropertyId,
                                property.Key,
                                property.Name,
                                property.ValueType,
                                property.Required,
                                property.DefaultValue,
                                property.Constraints,
                                property.IsEnabled,
                                property.SortOrder)).ToArray())).ToArray(),
                    model.Templates.Select(template =>
                        new KnowledgeTemplateUpdate(
                            template.TemplateId,
                            template.KnowledgeTypeId,
                            template.Key,
                            template.Name,
                            template.Description,
                            template.BodyTemplate,
                            template.Fields,
                            template.IsEnabled,
                            template.SortOrder)).ToArray(),
                    model.RelationTypes.Select(relation =>
                        new RelationTypeUpdate(
                            relation.RelationTypeId,
                            relation.Key,
                            relation.Name,
                            relation.Description,
                            relation.IsDirected,
                            relation.IsEnabled,
                            relation.SortOrder)).ToArray(),
                    model.SourceRules.Select(rule => new SourceRuleUpdate(
                        rule.SourceRuleId,
                        rule.Kind,
                        rule.MetadataSchema,
                        rule.SupportedLocatorTypes,
                        rule.IsEnabled)).ToArray(),
                    model.Workflows.Select(workflow => new WorkflowUpdate(
                        workflow.WorkflowId,
                        workflow.Key,
                        workflow.Name,
                        workflow.Description,
                        workflow.TargetObjectTypes,
                        workflow.Constraints,
                        workflow.IsEnabled,
                        workflow.SortOrder)).ToArray());
                var replaced = await KnowledgeModelStore.ReplaceAsync(
                    context,
                    model.Version,
                    update,
                    fixture.TimeProvider.GetUtcNow(),
                    token);
                await context.CommitTransactionAsync(token);
                return replaced;
            });

        Assert.Equal(2, result.Version);
    }

    [Fact]
    public async Task ReplaceRejectsMalformedConstraintsAndSourceSchema()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var model = await fixture.QueryAsync(
            context => KnowledgeModelStore.GetAsync(
                context,
                CancellationToken.None));
        var firstType = model.KnowledgeTypes[0];
        var malformedProperty = new KnowledgeTypeUpdate(
            firstType.TypeId,
            firstType.Key,
            firstType.Name,
            firstType.Description,
            firstType.IsEnabled,
            firstType.SortOrder,
            [
                new KnowledgePropertyUpdate(
                    null,
                    "status",
                    "Status",
                    "enum",
                    false,
                    null,
                    JsonSerializer.SerializeToElement(
                        new { options = "red" }),
                    true,
                    10)
            ]);
        var propertyUpdate = ToUpdate(model) with
        {
            KnowledgeTypes =
            [
                malformedProperty,
                .. model.KnowledgeTypes.Skip(1).Select(ToUpdate)
            ]
        };

        var propertyError = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.QueryAsync(context => KnowledgeModelStore.ReplaceAsync(
                context,
                model.Version,
                propertyUpdate,
                fixture.TimeProvider.GetUtcNow(),
                CancellationToken.None)));
        Assert.Equal(
            VaultErrorCode.KnowledgeModelInvalid,
            propertyError.Code);

        var malformedRule = model.SourceRules[0] with
        {
            MetadataSchema = JsonSerializer.SerializeToElement(
                new
                {
                    required = "url",
                    properties = new
                    {
                        url = new { type = "string" }
                    },
                    additionalProperties = false
                })
        };
        var ruleUpdate = ToUpdate(model) with
        {
            SourceRules =
            [
                ToUpdate(malformedRule),
                .. model.SourceRules.Skip(1).Select(ToUpdate)
            ]
        };
        var ruleError = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.QueryAsync(context => KnowledgeModelStore.ReplaceAsync(
                context,
                model.Version,
                ruleUpdate,
                fixture.TimeProvider.GetUtcNow(),
                CancellationToken.None)));
        Assert.Equal(VaultErrorCode.KnowledgeModelInvalid, ruleError.Code);

        var invalidDate = new KnowledgeTypeUpdate(
            firstType.TypeId,
            firstType.Key,
            firstType.Name,
            firstType.Description,
            firstType.IsEnabled,
            firstType.SortOrder,
            [
                new KnowledgePropertyUpdate(
                    null,
                    "publishedOn",
                    "Published on",
                    "date",
                    false,
                    JsonSerializer.SerializeToElement("not-a-date"),
                    JsonSerializer.SerializeToElement(new { }),
                    true,
                    10)
            ]);
        var dateUpdate = ToUpdate(model) with
        {
            KnowledgeTypes =
            [
                invalidDate,
                .. model.KnowledgeTypes.Skip(1).Select(ToUpdate)
            ]
        };
        var dateError = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.QueryAsync(context => KnowledgeModelStore.ReplaceAsync(
                context,
                model.Version,
                dateUpdate,
                fixture.TimeProvider.GetUtcNow(),
                CancellationToken.None)));
        Assert.Equal(VaultErrorCode.KnowledgeModelInvalid, dateError.Code);
    }

    private static KnowledgeModelUpdate ToUpdate(KnowledgeModelSnapshot model) =>
        new(
            model.KnowledgeTypes.Select(ToUpdate).ToArray(),
            model.Templates.Select(template =>
                new KnowledgeTemplateUpdate(
                    template.TemplateId,
                    template.KnowledgeTypeId,
                    template.Key,
                    template.Name,
                    template.Description,
                    template.BodyTemplate,
                    template.Fields,
                    template.IsEnabled,
                    template.SortOrder)).ToArray(),
            model.RelationTypes.Select(relation =>
                new RelationTypeUpdate(
                    relation.RelationTypeId,
                    relation.Key,
                    relation.Name,
                    relation.Description,
                    relation.IsDirected,
                    relation.IsEnabled,
                    relation.SortOrder)).ToArray(),
            model.SourceRules.Select(ToUpdate).ToArray(),
            model.Workflows.Select(workflow =>
                new WorkflowUpdate(
                    workflow.WorkflowId,
                    workflow.Key,
                    workflow.Name,
                    workflow.Description,
                    workflow.TargetObjectTypes,
                    workflow.Constraints,
                    workflow.IsEnabled,
                    workflow.SortOrder)).ToArray());

    private static KnowledgeTypeUpdate ToUpdate(
        KnowledgeTypeDefinition type) =>
        new(
            type.TypeId,
            type.Key,
            type.Name,
            type.Description,
            type.IsEnabled,
            type.SortOrder,
            type.Properties.Select(property =>
                new KnowledgePropertyUpdate(
                    property.PropertyId,
                    property.Key,
                    property.Name,
                    property.ValueType,
                    property.Required,
                    property.DefaultValue,
                    property.Constraints,
                    property.IsEnabled,
                    property.SortOrder)).ToArray());

    private static SourceRuleUpdate ToUpdate(SourceRuleDefinition rule) =>
        new(
            rule.SourceRuleId,
            rule.Kind,
            rule.MetadataSchema,
            rule.SupportedLocatorTypes,
            rule.IsEnabled);
}
