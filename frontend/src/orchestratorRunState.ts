import { clearByPrefix } from './storageKeys'

/** Durable, user-scoped state for an active root orchestrator run.  The input itself
 * is deliberately not stored: a non-cryptographic fingerprint is only used to decide
 * whether a retry is the same logical request. */
export interface StoredOrchestratorRun {
  version: 1
  orchestratorId: string
  runId: string | null
  conversationId: string
  startKey: string
  messageFingerprint: string
  eventCursor: number
  cancelKey: string | null
  cancelAccepted: boolean
}

const ORCHESTRATOR_RUN_STORAGE_PREFIX = 'springai-orchestrator-runs:active'

export function orchestratorRunStorageKey(scope: string, orchestratorId: string): string {
  return `${ORCHESTRATOR_RUN_STORAGE_PREFIX}:${encodeURIComponent(scope)}:${encodeURIComponent(orchestratorId)}`
}

export function messageFingerprint(message: string): string {
  let hash = 0x811c9dc5
  for (let index = 0; index < message.length; index += 1) {
    hash ^= message.charCodeAt(index)
    hash = Math.imul(hash, 0x01000193)
  }
  return (hash >>> 0).toString(16)
}

function valid(value: unknown, orchestratorId: string): value is StoredOrchestratorRun {
  if (!value || typeof value !== 'object') return false
  const item = value as Partial<StoredOrchestratorRun>
  return item.version === 1 &&
    item.orchestratorId === orchestratorId &&
    (typeof item.runId === 'string' || item.runId === null) &&
    typeof item.conversationId === 'string' && item.conversationId.length > 0 &&
    typeof item.startKey === 'string' && item.startKey.length > 0 &&
    typeof item.messageFingerprint === 'string' &&
    typeof item.eventCursor === 'number' && Number.isSafeInteger(item.eventCursor) && item.eventCursor >= 0 &&
    (typeof item.cancelKey === 'string' || item.cancelKey === null) &&
    typeof item.cancelAccepted === 'boolean'
}

export function readOrchestratorRunState(
  scope: string,
  orchestratorId: string,
  storage: Storage | undefined = globalThis.localStorage,
): StoredOrchestratorRun | null {
  if (!storage) return null
  const key = orchestratorRunStorageKey(scope, orchestratorId)
  try {
    const raw = storage.getItem(key)
    if (!raw) return null
    const value: unknown = JSON.parse(raw)
    if (valid(value, orchestratorId)) return value
    storage.removeItem(key)
  } catch {
    // Storage is optional. A malformed stale record must not prevent the console loading.
  }
  return null
}

export function writeOrchestratorRunState(
  scope: string,
  value: StoredOrchestratorRun,
  storage: Storage | undefined = globalThis.localStorage,
): void {
  if (!storage) return
  try {
    storage.setItem(orchestratorRunStorageKey(scope, value.orchestratorId), JSON.stringify(value))
  } catch {
    // The in-memory run is still usable if browser storage is unavailable or full.
  }
}

export function clearOrchestratorRunState(
  scope: string,
  orchestratorId: string,
  storage: Storage | undefined = globalThis.localStorage,
): void {
  try {
    storage?.removeItem(orchestratorRunStorageKey(scope, orchestratorId))
  } catch {
    // Best effort only.
  }
}

export function clearOrchestratorRunStorage(storage: Storage | undefined = globalThis.localStorage): void {
  clearByPrefix(storage, `${ORCHESTRATOR_RUN_STORAGE_PREFIX}:`)
}
