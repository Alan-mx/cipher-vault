import type { DocumentDraftDetail } from "../types/api";

type JsonRecord = Record<string, unknown>;

const isRecord = (value: unknown): value is JsonRecord =>
  typeof value === "object" && value !== null && !Array.isArray(value);

const readOperations = (payload: JsonRecord): JsonRecord[] =>
  Array.isArray(payload.operations) ? payload.operations.filter(isRecord) : [];

export const reviewContextFromPayload = (
  payload: JsonRecord,
): unknown | undefined =>
  Object.prototype.hasOwnProperty.call(payload, "reviewContext") &&
  payload.reviewContext !== null
    ? payload.reviewContext
    : undefined;

export const buildEditedUpdatePayload = (
  detail: DocumentDraftDetail,
  editedContent: string,
): JsonRecord => {
  const operations = readOperations(detail.payload)
    .filter(
      (operation) =>
        operation.type !== "replace_body" && operation.type !== "append_body",
    )
    .map((operation) => ({ ...operation }));
  operations.push({
    type: "replace_body",
    content: editedContent,
  });

  const payload: JsonRecord = { operations };
  const reviewContext = reviewContextFromPayload(detail.payload);
  if (reviewContext !== undefined) {
    payload.reviewContext = reviewContext;
  }
  return payload;
};
