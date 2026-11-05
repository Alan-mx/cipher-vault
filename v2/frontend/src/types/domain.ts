export type UtcTimestamp = string;

export type VaultState =
  | "uninitialized"
  | "initializing"
  | "locked"
  | "unlocking"
  | "unlocked"
  | "locking"
  | "lock_failed";

export type LockReason =
  | "none"
  | "user_requested"
  | "idle_timeout"
  | "process_exit"
  | "runtime_failure";

export interface ApiErrorBody {
  code: string;
  message: string;
  details: Record<string, unknown>;
  correlationId: string;
}

export class ApiClientError extends Error {
  readonly httpStatus: number;
  readonly code: string;
  readonly details: Record<string, unknown>;
  readonly correlationId: string;
  readonly retryAfterSeconds?: number;

  constructor(
    httpStatus: number,
    body: Partial<ApiErrorBody>,
    retryAfterSeconds?: number,
  ) {
    super(body.message ?? "The request failed.");
    this.name = "ApiClientError";
    this.httpStatus = httpStatus;
    this.code = body.code ?? "internal_error";
    this.details = body.details ?? {};
    this.correlationId = body.correlationId ?? "";
    if (retryAfterSeconds !== undefined) {
      this.retryAfterSeconds = retryAfterSeconds;
    }
  }
}

export class StaleSessionResponseError extends Error {
  constructor() {
    super("The response belongs to an invalidated Vault session.");
    this.name = "StaleSessionResponseError";
  }
}
