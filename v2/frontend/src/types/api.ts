import type { LockReason, UtcTimestamp, VaultState } from "./domain";

export interface VaultStatus {
  state: VaultState;
  isInitialized: boolean;
  isUnlocked: boolean;
  startedAtUtc?: UtcTimestamp | null;
  lastActivityAtUtc?: UtcTimestamp | null;
  idleExpiresAtUtc?: UtcTimestamp | null;
  idleTimeoutMinutes: number;
  idleRemainingSeconds: number | null;
  lockReason: LockReason;
}

export interface AntiforgeryTokenResponse {
  token: string;
}

export interface McpTokenStatus {
  configured: boolean;
  version?: number | null;
  createdAtUtc?: UtcTimestamp | null;
  rotatedAtUtc?: UtcTimestamp | null;
}

export interface McpTokenRotateResponse {
  token: string;
  version: number;
  createdAtUtc: UtcTimestamp;
  rotatedAtUtc: UtcTimestamp;
}

export type BackupFileStatus = "manifest_readable" | "invalid" | "verified";

export interface BackupListItem {
  fileName: string;
  bytes: number;
  modifiedAtUtc: UtcTimestamp;
  createdAtUtc?: UtcTimestamp | null;
  formatVersion?: number | null;
  status: BackupFileStatus;
  errorCode?: string | null;
}

export interface BackupListResponse {
  items: BackupListItem[];
  nextCursor?: string | null;
}

export interface BackupCreateResponse {
  backup: BackupListItem;
}

export type GitBackupState =
  | "disabled"
  | "idle"
  | "pending"
  | "running"
  | "retry_wait"
  | "rotation_required"
  | "failed";

export interface GitBackupStatus {
  state: GitBackupState;
  pending: boolean;
  pushPending: boolean;
  nextAttemptAtUtc?: UtcTimestamp | null;
  lastSuccessAtUtc?: UtcTimestamp | null;
  lastCommitShortSha?: string | null;
  lastPushAtUtc?: UtcTimestamp | null;
  lastErrorCode?: string | null;
  rotationRequired: boolean;
  repositoryObjectBytes?: number | null;
}

export interface BackupQuery {
  pageSize?: number;
  cursor?: string;
}

export interface VersionInfo {
  productVersion: string;
  commit: string;
  rid: string;
  runtimeVersion: string;
  aspNetCoreVersion: string;
}
