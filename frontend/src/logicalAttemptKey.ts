import { clearByPrefix } from './storageKeys'

interface StoredAttempt {
  version: 1
  fingerprint: string
  key: string
  ambiguous: boolean
}

export const AGENT_RUN_ATTEMPT_STORAGE_PREFIX = 'springai-agent-runs:idempotency'
/** Operations governance(O1)寫入動作(override 等)的 idempotency key 前綴,同樣要在登出時清除。 */
export const OPERATIONS_ATTEMPT_STORAGE_PREFIX = 'springai-operations:'
/** D7 approval 決策(ApprovalInbox)的 idempotency key 前綴,同樣要在登出時清除。 */
export const RUN_APPROVAL_ATTEMPT_STORAGE_PREFIX = 'springai-run-approvals:'

export interface PendingCancelRun {
  runId: string
  accepted: boolean
}

function parseAttempt(raw: string | null): StoredAttempt | null {
  if (raw === null) return null
  try {
    const value = JSON.parse(raw) as Partial<StoredAttempt>
    return value.version === 1 &&
      typeof value.fingerprint === 'string' &&
      typeof value.key === 'string' &&
      value.key.length > 0 &&
      typeof value.ambiguous === 'boolean'
      ? (value as StoredAttempt)
      : null
  } catch {
    return null
  }
}

export function getSessionStorage(): Storage | undefined {
  try {
    return globalThis.sessionStorage
  } catch {
    return undefined
  }
}

export function clearLogicalAttemptStorage(storage = getSessionStorage()): void {
  clearByPrefix(storage, `${AGENT_RUN_ATTEMPT_STORAGE_PREFIX}:`)
  clearByPrefix(storage, OPERATIONS_ATTEMPT_STORAGE_PREFIX)
  clearByPrefix(storage, RUN_APPROVAL_ATTEMPT_STORAGE_PREFIX)
}

function pendingCancelStorageKey(agentId: string): string {
  return `${AGENT_RUN_ATTEMPT_STORAGE_PREFIX}:pending-cancel:${encodeURIComponent(agentId)}`
}

export function readPendingCancelRun(
  agentId: string,
  storage = getSessionStorage(),
): PendingCancelRun | null {
  if (!storage) return null
  const key = pendingCancelStorageKey(agentId)
  try {
    const raw = storage.getItem(key)
    if (raw === null) return null
    const value = JSON.parse(raw) as Partial<PendingCancelRun> & { version?: unknown }
    if (
      value.version === 1 &&
      typeof value.runId === 'string' &&
      value.runId.length > 0 &&
      typeof value.accepted === 'boolean'
    ) {
      return { runId: value.runId, accepted: value.accepted }
    }
    storage.removeItem(key)
  } catch {
    try {
      storage.removeItem(key)
    } catch {
      // Storage is unavailable; restoration safely fails closed.
    }
  }
  return null
}

export function writePendingCancelRun(
  agentId: string,
  pending: PendingCancelRun,
  storage = getSessionStorage(),
): void {
  if (!storage) return
  try {
    storage.setItem(
      pendingCancelStorageKey(agentId),
      JSON.stringify({ version: 1, ...pending }),
    )
  } catch {
    // The live component still retains the state and stable attempt key in memory.
  }
}

export function clearPendingCancelRun(agentId: string, storage = getSessionStorage()): void {
  if (!storage) return
  try {
    storage.removeItem(pendingCancelStorageKey(agentId))
  } catch {
    // Cleanup is best-effort when storage is unavailable.
  }
}

export class LogicalAttemptKey {
  private attempt: StoredAttempt | null = null
  private readonly createKey: () => string
  private readonly storage: Storage | undefined
  private readonly storageKey: string

  constructor(createKey: () => string, storage?: Storage, storageKey = '') {
    this.createKey = createKey
    this.storage = storage
    this.storageKey = storageKey
    this.attempt = this.read()
  }

  keyFor(identity: readonly unknown[]): string {
    const fingerprint = JSON.stringify(identity)
    if (this.attempt?.fingerprint !== fingerprint) {
      this.attempt = {
        version: 1,
        fingerprint,
        key: this.createKey(),
        ambiguous: false,
      }
      this.write()
    }
    return this.attempt.key
  }

  markAmbiguous(identity: readonly unknown[], key: string): void {
    if (this.matches(identity, key) && this.attempt) {
      this.attempt.ambiguous = true
      this.write()
    }
  }

  isAmbiguous(identity: readonly unknown[]): boolean {
    return this.attempt?.fingerprint === JSON.stringify(identity) && this.attempt.ambiguous
  }

  consume(identity: readonly unknown[], key: string): void {
    if (this.matches(identity, key)) this.clear()
  }

  consumeIdentity(identity: readonly unknown[]): void {
    if (this.attempt?.fingerprint === JSON.stringify(identity)) this.clear()
  }

  rotate(identity: readonly unknown[]): boolean {
    if (this.isAmbiguous(identity)) return false
    this.clear()
    return true
  }

  private matches(identity: readonly unknown[], key: string): boolean {
    return this.attempt?.fingerprint === JSON.stringify(identity) && this.attempt.key === key
  }

  private read(): StoredAttempt | null {
    if (!this.storage || !this.storageKey) return null
    try {
      const raw = this.storage.getItem(this.storageKey)
      const parsed = parseAttempt(raw)
      if (raw !== null && parsed === null) this.storage.removeItem(this.storageKey)
      return parsed
    } catch {
      return null
    }
  }

  private write(): void {
    if (!this.storage || !this.storageKey || !this.attempt) return
    try {
      this.storage.setItem(this.storageKey, JSON.stringify(this.attempt))
    } catch {
      // Storage may be disabled or full; the in-memory attempt remains safe for this mount.
    }
  }

  private clear(): void {
    this.attempt = null
    if (!this.storage || !this.storageKey) return
    try {
      this.storage.removeItem(this.storageKey)
    } catch {
      // Storage may be disabled; clearing the in-memory attempt is still fail-safe.
    }
  }
}
