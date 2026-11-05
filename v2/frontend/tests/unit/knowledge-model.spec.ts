import { describe, expect, it } from "vitest";
import type { KnowledgeModel } from "../../src/types/knowledge-base";
import { toKnowledgeModelUpdateRequest } from "../../src/utils/knowledge-model";

const model: KnowledgeModel = {
  version: 3,
  updatedAt: "2026-11-05T00:00:00Z",
  knowledgeTypes: [
    {
      typeId: "type-id",
      key: "note",
      name: "Note",
      description: null,
      isEnabled: true,
      sortOrder: 0,
      properties: [
        {
          propertyId: null,
          key: "status",
          name: "Status",
          valueType: "string",
          required: true,
          defaultValue: "raw",
          constraints: { maxLength: 20 },
          isEnabled: true,
          sortOrder: 0,
        },
      ],
    },
  ],
  templates: [
    {
      templateId: null,
      knowledgeTypeId: "type-id",
      key: "note-template",
      name: "Note template",
      description: null,
      bodyTemplate: "# {{title}}",
      fields: [],
      isEnabled: true,
      sortOrder: 0,
    },
  ],
  relationTypes: [
    {
      relationTypeId: null,
      key: "supports",
      name: "Supports",
      description: null,
      isDirected: true,
      isEnabled: true,
      sortOrder: 0,
    },
  ],
  sourceRules: [
    {
      sourceRuleId: "source-rule-id",
      kind: "web_page",
      metadataSchema: { required: ["url"] },
      supportedLocatorTypes: ["section", "paragraph"],
      isEnabled: true,
    },
  ],
  workflows: [
    {
      workflowId: null,
      key: "extract",
      name: "Extract",
      description: null,
      targetObjectTypes: ["knowledge"],
      constraints: {},
      isEnabled: true,
      sortOrder: 0,
    },
  ],
};

describe("toKnowledgeModelUpdateRequest", () => {
  it("maps read-model keys to the server write contract", () => {
    const body = toKnowledgeModelUpdateRequest(model);

    expect(body.knowledgeTypes[0]).toMatchObject({
      typeId: "type-id",
      typeKey: "note",
      properties: [
        {
          propertyId: null,
          propertyKey: "status",
          constraints: { maxLength: 20 },
        },
      ],
    });
    expect(body.templates[0]).toMatchObject({
      templateId: null,
      templateKey: "note-template",
    });
    expect(body.relationTypes[0]).toMatchObject({
      relationTypeId: null,
      relationKey: "supports",
    });
    expect(body.sourceRules[0]).toMatchObject({
      sourceRuleId: "source-rule-id",
      sourceKind: "web_page",
    });
    expect(body.workflows[0]).toMatchObject({
      workflowId: null,
      workflowKey: "extract",
    });
    expect(body).not.toHaveProperty("knowledgeTypes.0.key");
  });
});
