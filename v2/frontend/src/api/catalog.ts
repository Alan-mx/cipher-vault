import type { CatalogPage, CatalogQuery, TagPage } from "../types/api";
import { apiClient } from "./client";

export const catalogApi = {
  list: (query: CatalogQuery = {}) =>
    apiClient.request<CatalogPage>("/api/documents", {
      query: {
        folder: query.folder,
        format: query.format,
        tags: query.tags,
        orphanOnly: query.orphanOnly,
        pageSize: query.pageSize,
        cursor: query.cursor,
      },
    }),

  tags: (pageSize = 50, cursor?: string) =>
    apiClient.request<TagPage>("/api/tags", {
      query: { pageSize, cursor },
    }),
};
