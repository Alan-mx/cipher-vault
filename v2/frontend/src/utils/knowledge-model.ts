import type {
  KnowledgeModel,
  KnowledgeProperty,
  KnowledgeTemplate,
  RelationType,
  SourceRule,
  Workflow,
} from "../types/knowledge-base";

export const toKnowledgeModelUpdateRequest = (model: KnowledgeModel) => ({
  knowledgeTypes: model.knowledgeTypes.map((type) => ({
    typeId: type.typeId || null,
    typeKey: type.key,
    name: type.name,
    description: type.description,
    isEnabled: type.isEnabled,
    sortOrder: type.sortOrder,
    properties: type.properties.map(toPropertyRequest),
  })),
  templates: model.templates.map(toTemplateRequest),
  relationTypes: model.relationTypes.map(toRelationRequest),
  sourceRules: model.sourceRules.map(toSourceRuleRequest),
  workflows: model.workflows.map(toWorkflowRequest),
});

const toPropertyRequest = (property: KnowledgeProperty) => ({
  propertyId: property.propertyId || null,
  propertyKey: property.key,
  name: property.name,
  valueType: property.valueType,
  required: property.required,
  defaultValue: property.defaultValue ?? null,
  constraints: property.constraints,
  isEnabled: property.isEnabled,
  sortOrder: property.sortOrder,
});

const toTemplateRequest = (template: KnowledgeTemplate) => ({
  templateId: template.templateId || null,
  knowledgeTypeId: template.knowledgeTypeId,
  templateKey: template.key,
  name: template.name,
  description: template.description,
  bodyTemplate: template.bodyTemplate,
  fields: template.fields,
  isEnabled: template.isEnabled,
  sortOrder: template.sortOrder,
});

const toRelationRequest = (relation: RelationType) => ({
  relationTypeId: relation.relationTypeId || null,
  relationKey: relation.key,
  name: relation.name,
  description: relation.description,
  isDirected: relation.isDirected,
  isEnabled: relation.isEnabled,
  sortOrder: relation.sortOrder,
});

const toSourceRuleRequest = (rule: SourceRule) => ({
  sourceRuleId: rule.sourceRuleId || null,
  sourceKind: rule.kind,
  metadataSchema: rule.metadataSchema,
  supportedLocatorTypes: rule.supportedLocatorTypes,
  isEnabled: rule.isEnabled,
});

const toWorkflowRequest = (workflow: Workflow) => ({
  workflowId: workflow.workflowId || null,
  workflowKey: workflow.key,
  name: workflow.name,
  description: workflow.description,
  targetObjectTypes: workflow.targetObjectTypes,
  constraints: workflow.constraints,
  isEnabled: workflow.isEnabled,
  sortOrder: workflow.sortOrder,
});
