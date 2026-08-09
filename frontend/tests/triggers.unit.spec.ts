import { expect, test } from 'vitest'
import {
  cancelTrigger,
  createTrigger,
  encodeTriggerCreate,
  listTriggerOccurrences,
  listTriggers,
  normalizeTrigger,
  normalizeTriggerOccurrencePage,
  normalizeTriggers,
} from '../src/api/triggers'
import {
  localInputToUtcIso,
  occurrenceStatusLabel,
  shortId,
  triggerStatusChipClass,
  triggerStatusLabel,
} from '../src/triggerDisplay'

// Fixture 逐字抄自 backend 的實際回應鍵(backend/tests/Backend.Api.Tests/TriggerApiTests.cs
// 的 DTO allowlist 斷言):TriggerResponse 沒有 root_run_id,清單一律包在 {items:[...]}。
const WIRE_TRIGGER = {
  id: 'trigger-1',
  name: '週一報表',
  description: '每週報表',
  orchestrator_id: 'orch-1',
  orchestrator_revision: 3,
  input_mapping: { message: '產生報表' },
  fire_at: '2026-08-10T02:30:00Z',
  misfire_window_seconds: 300,
  status: 'scheduled',
  created_by: 'admin',
  created_at: '2026-08-09T00:00:00Z',
  updated_at: '2026-08-09T00:00:00Z',
}

// TriggerOccurrenceResponse 的實際鍵:scheduled_for(不是 scheduled_fire_at),原因碼併在
// status 前綴裡(後端刻意不開 reason_code),沒有 fired_at。
const WIRE_OCCURRENCE = {
  id: 'occ-1',
  trigger_id: 'trigger-1',
  scheduled_for: '2026-08-10T02:30:00Z',
  status: 'skipped_misfired',
  root_run_id: null,
  created_at: '2026-08-10T02:30:01Z',
  updated_at: '2026-08-10T02:35:00Z',
}

function stubFetch(
  body: unknown,
  headers: Record<string, string> = {},
): { calls: { url: string; init?: RequestInit }[]; restore: () => void } {
  const originalFetch = globalThis.fetch
  const calls: { url: string; init?: RequestInit }[] = []
  globalThis.fetch = async (input, init) => {
    calls.push({ url: String(input), init })
    return new Response(JSON.stringify(body), {
      status: 200,
      headers: { 'Content-Type': 'application/json', ...headers },
    })
  }
  return { calls, restore: () => { globalThis.fetch = originalFetch } }
}

test.describe('O5 trigger DTO projection', () => {
  test('allowlists the safe fields and drops principal snapshot / lease / idempotency leakage', () => {
    const trigger = normalizeTrigger({
      ...WIRE_TRIGGER,
      principal: { capabilities: ['secret'] },
      lease_token: 'secret-lease',
      fencing_token: 42,
      idempotency_key: 'secret-key',
    })
    expect(trigger).toEqual({
      id: 'trigger-1',
      name: '週一報表',
      orchestratorId: 'orch-1',
      orchestratorRevision: 3,
      fireAt: '2026-08-10T02:30:00Z',
      status: 'scheduled',
      inputMapping: { message: '產生報表' },
      createdBy: 'admin',
    })
    expect(JSON.stringify(trigger)).not.toContain('secret')
  })

  test('drops rows without an id and reads the {items} envelope the backend actually returns', () => {
    expect(normalizeTrigger({ name: 'no id' })).toBeNull()
    // 沒有 input_mapping 時退回空物件,不是 undefined——渲染端不必再防一次。
    expect(normalizeTrigger({ id: 'trigger-2' })?.inputMapping).toEqual({})
    expect(normalizeTriggers({ items: [WIRE_TRIGGER, { name: 'no id' }] })).toHaveLength(1)
    expect(normalizeTriggers({ items: [] })).toEqual([])
    expect(normalizeTriggers(null)).toEqual([])
  })

  test('listTriggers unwraps the {items} envelope instead of expecting a bare array', async () => {
    const stub = stubFetch({ items: [WIRE_TRIGGER] })
    try {
      const rows = await listTriggers()
      expect(stub.calls[0].url).toBe('/api/admin/triggers')
      expect(rows).toHaveLength(1)
      expect(rows[0].orchestratorId).toBe('orch-1')
    } finally { stub.restore() }
  })

  test('the occurrence page keeps only ledger fields and reads the keyset cursor', () => {
    const page = normalizeTriggerOccurrencePage({
      items: [{ ...WIRE_OCCURRENCE, lease_token: 'secret-lease' }, { status: 'fired' }],
      has_more: true,
      next_cursor: 'c1',
    })
    expect(page.items).toEqual([{
      id: 'occ-1',
      scheduledFor: '2026-08-10T02:30:00Z',
      status: 'skipped_misfired',
      rootRunId: null,
      createdAt: '2026-08-10T02:30:01Z',
    }])
    expect(page.hasMore).toBe(true)
    expect(page.cursor).toBe('c1')
    expect(normalizeTriggerOccurrencePage(null)).toEqual({ items: [], hasMore: false, cursor: null })
  })
})

test.describe('O5 trigger requests', () => {
  test('the create body uses the backend TriggerCreateRequest field names', () => {
    expect(encodeTriggerCreate({
      name: '週一報表', orchestratorId: 'orch-1', orchestratorRevision: 3,
      fireAt: '2026-08-10T02:30:00.000Z', inputMapping: { message: '產生報表' },
    })).toEqual({
      name: '週一報表', orchestrator_id: 'orch-1', orchestrator_revision: 3,
      fire_at: '2026-08-10T02:30:00.000Z', input_mapping: { message: '產生報表' },
    })
  })

  test('createTrigger POSTs to the admin trigger route and normalizes the response', async () => {
    const stub = stubFetch(WIRE_TRIGGER)
    try {
      const created = await createTrigger({
        name: '週一報表', orchestratorId: 'orch-1', orchestratorRevision: 3,
        fireAt: '2026-08-10T02:30:00.000Z', inputMapping: { message: '產生報表' },
      })
      expect(stub.calls[0].url).toBe('/api/admin/triggers')
      expect(stub.calls[0].init?.method).toBe('POST')
      expect(created?.id).toBe('trigger-1')
    } finally { stub.restore() }
  })

  // 後端 cancel 是樂觀鎖寫入(缺 If-Match 直接 428),而清單回應不帶版本,
  // 所以取消一定要先讀單筆拿 ETag 再帶著它送出。
  test('cancelTrigger reads the current ETag and sends it back as If-Match', async () => {
    const stub = stubFetch(WIRE_TRIGGER, { ETag: '"7"' })
    try {
      await cancelTrigger('trigger-1')
      expect(stub.calls[0].url).toBe('/api/admin/triggers/trigger-1')
      expect(stub.calls[0].init?.method ?? 'GET').toBe('GET')
      expect(stub.calls[1].url).toBe('/api/admin/triggers/trigger-1/cancel')
      expect(stub.calls[1].init?.method).toBe('POST')
      expect(new Headers(stub.calls[1].init?.headers).get('If-Match')).toBe('"7"')
    } finally { stub.restore() }
  })

  test('listTriggerOccurrences encodes the id and forwards only the set keyset params', async () => {
    const stub = stubFetch({ items: [], has_more: false, next_cursor: null })
    try {
      await listTriggerOccurrences('trig er/1')
      expect(stub.calls[0].url).toBe('/api/admin/triggers/trig%20er%2F1/occurrences')
      await listTriggerOccurrences('trigger-1', { cursor: 'c1', limit: 20 })
      expect(stub.calls[1].url).toBe('/api/admin/triggers/trigger-1/occurrences?cursor=c1&limit=20')
    } finally { stub.restore() }
  })
})

test.describe('O5 trigger display helpers', () => {
  test('status dictionaries are Chinese for the backend enum values and pass unknown ones through', () => {
    expect(triggerStatusLabel('scheduled')).toBe('已排程')
    expect(triggerStatusLabel('misfired')).toBe('已錯過')
    expect(triggerStatusLabel('future-status')).toBe('future-status')
    expect(triggerStatusChipClass('fired')).toBe('chip--ready')
    expect(triggerStatusChipClass('failed')).toBe('chip--failed')
    expect(triggerStatusChipClass('future-status')).toBe('chip--skip')
    // 原因碼是併進 status 前綴的 bounded enum,不是獨立欄位——字典的 key 必須是後端真值。
    expect(occurrenceStatusLabel('claimed')).toBe('已認領')
    expect(occurrenceStatusLabel('skipped_misfired')).toBe('已略過(超過補觸發時限)')
    expect(occurrenceStatusLabel('failed_target_unpublished')).toBe('失敗(目標協作流程未發布)')
    expect(occurrenceStatusLabel('failed_dispatch_disabled')).toBe('失敗(多 Agent 協作派工未啟用)')
    expect(occurrenceStatusLabel('skipped_future')).toBe('skipped_future')
  })

  test('shortId truncates to eight characters and renders a dash when absent', () => {
    expect(shortId('12345678-aaaa-bbbb')).toBe('12345678')
    expect(shortId(null)).toBe('—')
  })

  test('the datetime-local value converts to an absolute UTC instant; blank/invalid stays null', () => {
    const iso = localInputToUtcIso('2026-08-10T10:30')
    // 本地時區未知,所以比對「同一個絕對時刻」而不是字面字串。
    expect(iso).not.toBeNull()
    expect(new Date(iso!).getTime()).toBe(new Date(2026, 7, 10, 10, 30).getTime())
    expect(iso).toBe(new Date(iso!).toISOString())
    expect(localInputToUtcIso('')).toBeNull()
    expect(localInputToUtcIso('   ')).toBeNull()
    expect(localInputToUtcIso('not-a-date')).toBeNull()
  })
})
