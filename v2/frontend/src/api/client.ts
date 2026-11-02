import {
  ApiClientError,
  StaleSessionResponseError,
  type ApiErrorBody,
} from "../types/domain";

type QueryScalar = string | number | boolean | null | undefined;
export type QueryValue = QueryScalar | readonly QueryScalar[];
export type QueryInput = Record<string, QueryValue>;

export interface ApiRequestOptions {
  method?: "GET" | "POST" | "PUT" | "DELETE";
  query?: QueryInput;
  body?: unknown;
  ifMatch?: number;
  signal?: AbortSignal;
  auth?: "none" | "cookie";
  csrf?: boolean;
  sessionGeneration?: number;
}

export interface ApiClientHooks {
  getCsrfToken: () => string | null;
  refreshCsrfToken: (expectedGeneration: number) => Promise<string>;
  currentSessionGeneration: () => number;
  currentAbortSignal?: () => AbortSignal;
  onSessionInvalid: (
    error: ApiClientError,
    expectedGeneration: number,
  ) => boolean;
}

const defaultHooks: ApiClientHooks = {
  getCsrfToken: () => null,
  refreshCsrfToken: async () => {
    throw new Error("CSRF provider is not configured.");
  },
  currentSessionGeneration: () => 0,
  onSessionInvalid: () => false,
};

let hooks = defaultHooks;

export const configureApiClient = (next: ApiClientHooks): void => {
  hooks = next;
};

export const buildQueryString = (query?: QueryInput): string => {
  if (!query) {
    return "";
  }

  const parameters = new URLSearchParams();
  for (const key of Object.keys(query).sort()) {
    const value = query[key];
    if (Array.isArray(value)) {
      for (const item of value) {
        appendQueryValue(parameters, key, item);
      }
    } else {
      appendQueryValue(parameters, key, value as QueryScalar);
    }
  }
  const encoded = parameters.toString();
  return encoded.length > 0 ? `?${encoded}` : "";
};

const appendQueryValue = (
  parameters: URLSearchParams,
  key: string,
  value: QueryScalar,
): void => {
  if (value === null || value === undefined || value === "") {
    return;
  }
  parameters.append(key, String(value));
};

const readRetryAfter = (response: Response): number | undefined => {
  const value = response.headers.get("Retry-After");
  if (!value) {
    return undefined;
  }
  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : undefined;
};

const readErrorBody = async (
  response: Response,
): Promise<Partial<ApiErrorBody>> => {
  try {
    return (await response.json()) as Partial<ApiErrorBody>;
  } catch {
    return {
      code: "invalid_response",
      message: "The server returned an unreadable error response.",
    };
  }
};

const createRequestInit = async (
  options: ApiRequestOptions,
  generation: number,
): Promise<RequestInit> => {
  ensureCurrentGeneration(generation);
  const headers = new Headers();
  const method = options.method ?? "GET";
  if (options.body !== undefined) {
    headers.set("Content-Type", "application/json");
  }
  if (options.ifMatch !== undefined) {
    headers.set("If-Match", `"${options.ifMatch}"`);
  }
  if (options.csrf) {
    let token = hooks.getCsrfToken();
    if (!token) {
      ensureCurrentGeneration(generation);
      token = await hooks.refreshCsrfToken(generation);
      ensureCurrentGeneration(generation);
    }
    headers.set("X-CSRF-TOKEN", token);
  }

  const sessionSignal = hooks.currentAbortSignal?.();
  const signal =
    options.signal && sessionSignal
      ? AbortSignal.any([options.signal, sessionSignal])
      : (options.signal ?? sessionSignal);
  return {
    method,
    credentials: "same-origin",
    cache: "no-store",
    headers,
    ...(signal ? { signal } : {}),
    ...(options.body !== undefined
      ? { body: JSON.stringify(options.body) }
      : {}),
  };
};

const ensureCurrentGeneration = (expectedGeneration: number): void => {
  if (hooks.currentSessionGeneration() !== expectedGeneration) {
    throw new StaleSessionResponseError();
  }
};

const execute = async (
  path: string,
  options: ApiRequestOptions,
  parse: (response: Response) => Promise<unknown>,
): Promise<unknown> => {
  const generation =
    options.sessionGeneration ?? hooks.currentSessionGeneration();
  const url = `${path}${buildQueryString(options.query)}`;
  let retriedCsrf = false;

  while (true) {
    ensureCurrentGeneration(generation);
    let response: Response;
    try {
      response = await fetch(url, await createRequestInit(options, generation));
    } catch (error) {
      if (error instanceof StaleSessionResponseError) {
        throw error;
      }
      ensureCurrentGeneration(generation);
      if (error instanceof DOMException && error.name === "AbortError") {
        throw error;
      }
      throw new ApiClientError(0, {
        code: "network_error",
        message: "Unable to reach CipherVault.Server.",
        details: {},
        correlationId: "",
      });
    }

    ensureCurrentGeneration(generation);
    if (!response.ok) {
      const body = await readErrorBody(response);
      ensureCurrentGeneration(generation);
      const error = new ApiClientError(
        response.status,
        body,
        readRetryAfter(response),
      );

      if (
        !retriedCsrf &&
        options.csrf &&
        response.status === 400 &&
        error.code === "antiforgery_invalid"
      ) {
        retriedCsrf = true;
        ensureCurrentGeneration(generation);
        await hooks.refreshCsrfToken(generation);
        ensureCurrentGeneration(generation);
        continue;
      }

      if (response.status === 401 || response.status === 423) {
        ensureCurrentGeneration(generation);
        hooks.onSessionInvalid(error, generation);
      }
      throw error;
    }

    let parsed: unknown;
    try {
      parsed = await parse(response);
    } catch (error) {
      if (error instanceof StaleSessionResponseError) {
        throw error;
      }
      ensureCurrentGeneration(generation);
      throw error;
    }
    ensureCurrentGeneration(generation);
    return parsed;
  }
};

export class ApiClient {
  async request<T>(path: string, options: ApiRequestOptions = {}): Promise<T> {
    return (await execute(path, options, async (response) => {
      try {
        return await response.json();
      } catch {
        throw new ApiClientError(response.status, {
          code: "invalid_response",
          message: "The server returned invalid JSON.",
          details: {},
          correlationId: response.headers.get("X-Correlation-ID") ?? "",
        });
      }
    })) as T;
  }

  async requestVoid(
    path: string,
    options: ApiRequestOptions = {},
  ): Promise<void> {
    await execute(path, options, async () => undefined);
  }
}

export const apiClient = new ApiClient();
