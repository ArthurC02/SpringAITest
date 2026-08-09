import { apiFetch, apiFetchWithEtag } from './http'
import { integer, object, text, type JsonObject } from '../wire'
import type { Trigger, TriggerCreateRequest, TriggerOccurrence, TriggerOccurrencePage } from '../types'

const base = '/api/admin/triggers'
const idPath = (id: string) => `${base}/${encodeURIComponent(id)}`

/**
 * 白名單投影:契約沒列出的欄位(principal 快照、lease/fencing token、idempotency)一律丟棄。
 * 鍵名逐字對齊 backend `TriggerResponse`(backend/src/Backend.Api/Triggers/TriggerDtos.cs);
 * 這條路徑只有 snake_case 一種 wire 形狀,不猜別名。
 */
export function normalizeTrigger(value: unknown): Trigger | null {
  const source = object(value)
  const id = text(source.id)
  if (!id) return null
  return {
    id,
    name: text(source.name) ?? '',
    orchestratorId: text(source.orchestrator_id),
    orchestratorRevision: integer(source.orchestrator_revision),
    fireAt: text(source.fire_at),
    status: text(source.status) ?? 'unknown',
    inputMapping: object(source.input_mapping),
    createdBy: text(source.created_by),
  }
}

export function normalizeTriggers(value: unknown): Trigger[] {
  const raw = (object(value).items as unknown[] | undefined) ?? []
  return raw.flatMap((item) => {
    const trigger = normalizeTrigger(item)
    return trigger ? [trigger] : []
  })
}

/** 對齊 backend `TriggerOccurrenceResponse`:原因碼併在 `status` 前綴,沒有獨立 reason_code。 */
function normalizeOccurrence(value: unknown): TriggerOccurrence | null {
  const source = object(value)
  const id = text(source.id)
  if (!id) return null
  return {
    id,
    scheduledFor: text(source.scheduled_for),
    status: text(source.status) ?? 'unknown',
    rootRunId: text(source.root_run_id),
    createdAt: text(source.created_at),
  }
}

export function normalizeTriggerOccurrencePage(value: unknown): TriggerOccurrencePage {
  const source = object(value)
  const raw = (source.items as unknown[] | undefined) ?? []
  return {
    items: raw.flatMap((item) => {
      const occurrence = normalizeOccurrence(item)
      return occurrence ? [occurrence] : []
    }),
    hasMore: source.has_more === true,
    cursor: text(source.next_cursor),
  }
}

/** 對齊 backend `TriggerCreateRequest`;`fire_at` 必須已是絕對 UTC 時刻。 */
export function encodeTriggerCreate(request: TriggerCreateRequest): JsonObject {
  return {
    name: request.name,
    orchestrator_id: request.orchestratorId,
    orchestrator_revision: request.orchestratorRevision,
    fire_at: request.fireAt,
    input_mapping: request.inputMapping,
  }
}

export async function listTriggers(): Promise<Trigger[]> {
  return normalizeTriggers(await apiFetch<unknown>(base))
}

export async function createTrigger(request: TriggerCreateRequest): Promise<Trigger | null> {
  return normalizeTrigger(await apiFetch<unknown>(base, {
    method: 'POST',
    body: JSON.stringify(encodeTriggerCreate(request)),
  }))
}

/**
 * 取消只停未來的 claim;已建立的 root 照 snapshot 跑完(計畫 §6.3 rollback 語意)。
 * 後端的 cancel 是樂觀鎖寫入(缺 If-Match 直接 428),而清單回應不帶版本,
 * 所以先讀單筆取當前 ETag 再帶回去;期間被他人改動就是 409,由呼叫端提示重讀。
 */
export async function cancelTrigger(id: string): Promise<Trigger | null> {
  const { etag } = await apiFetchWithEtag<unknown>(idPath(id))
  return normalizeTrigger(await apiFetch<unknown>(`${idPath(id)}/cancel`, {
    method: 'POST',
    headers: etag ? { 'If-Match': etag } : undefined,
  }))
}

export async function listTriggerOccurrences(
  id: string,
  options: { cursor?: string | null; limit?: number } = {},
): Promise<TriggerOccurrencePage> {
  const query = new URLSearchParams()
  if (options.cursor) query.set('cursor', options.cursor)
  if (options.limit) query.set('limit', String(options.limit))
  const qs = query.toString()
  return normalizeTriggerOccurrencePage(await apiFetch<unknown>(`${idPath(id)}/occurrences${qs ? `?${qs}` : ''}`))
}
