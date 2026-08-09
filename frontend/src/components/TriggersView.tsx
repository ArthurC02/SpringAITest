import { useCallback, useRef, useState } from 'react'
import { cancelTrigger, createTrigger, listTriggerOccurrences, listTriggers } from '../api/triggers'
import { listOrchestrators } from '../api/orchestrators'
import { useResource } from '../hooks/useResource'
import { fmtDate } from '../format'
import {
  localInputToUtcIso,
  occurrenceStatusLabel,
  shortId,
  triggerStatusChipClass,
  triggerStatusLabel,
} from '../triggerDisplay'
import type { OrchestratorSummary, Trigger, TriggerOccurrence } from '../types'
import CatalogPicker from './CatalogPicker'
import { useConfirm } from './ConfirmDialog'
import ErrorText from './ErrorText'
import Skeleton from './Skeleton'
import { runWithToast, useToast } from './Toast'

/**
 * input mapping 的結構化鍵值編輯器 + 進階 JSON 逃生門(比照 AgentEditor 的 Output contract
 * 互動模式)。文字是唯一真相來源,巢狀/陣列值只能整鍵移除,要改值就切到 JSON 模式。
 */
function InputMappingEditor({
  text,
  parseError,
  disabled,
  onChange,
}: {
  text: string
  parseError: string | null
  disabled: boolean
  onChange: (next: string) => void
}) {
  const [advanced, setAdvanced] = useState(false)
  const [newKey, setNewKey] = useState('')
  const [newValue, setNewValue] = useState('')
  const value = parseInputMapping(text).value ?? {}
  const entries = Object.entries(value)

  function setEntry(key: string, val: unknown) {
    onChange(JSON.stringify({ ...value, [key]: val }, null, 2))
  }
  function removeEntry(key: string) {
    const next = { ...value }
    delete next[key]
    onChange(JSON.stringify(next, null, 2))
  }
  function addEntry() {
    const key = newKey.trim()
    if (!key || Object.prototype.hasOwnProperty.call(value, key)) return
    setEntry(key, newValue)
    setNewKey('')
    setNewValue('')
  }

  return (
    <div className="field">
      <p className="muted agent-set__hint">
        Input mapping(觸發時要傳給協作流程的固定輸入;必須包含非空白的 message 鍵,建立當下就會被驗證形狀,不得放入密碼或標頭)
      </p>
      {advanced ? (
        <>
          <label htmlFor="trigger-input-mapping">Input mapping(進階 JSON 模式)</label>
          <textarea
            id="trigger-input-mapping"
            className="textarea code-textarea"
            value={text}
            disabled={disabled}
            aria-invalid={!!parseError}
            onChange={(e) => onChange(e.target.value)}
          />
        </>
      ) : (
        <>
          {parseError && (
            <p className="field-error" role="alert">
              目前的內容不是合法 JSON,暫時無法以結構化模式顯示;請切到進階 JSON 模式修正。
            </p>
          )}
          {entries.length === 0 ? (
            <p className="agent-set__empty" role="note">尚未設定任何欄位。</p>
          ) : (
            <ul className="agent-kv">
              {entries.map(([key, val]) => (
                <li key={key} className="agent-kv__row">
                  <span className="agent-kv__key">{key}</span>
                  {typeof val === 'string' ? (
                    <input
                      className="input"
                      aria-label={`${key} 的值`}
                      value={val}
                      disabled={disabled}
                      onChange={(e) => setEntry(key, e.target.value)}
                    />
                  ) : (
                    <span className="muted">複合值,請切到進階 JSON 模式編輯</span>
                  )}
                  {!disabled && (
                    <button
                      type="button"
                      className="agent-set__chip-x"
                      aria-label={`移除 ${key}`}
                      onClick={() => removeEntry(key)}
                    >
                      ×
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )}
          {!disabled && (
            <div className="agent-set__add">
              <input className="input" placeholder="鍵" aria-label="新增鍵" value={newKey} onChange={(e) => setNewKey(e.target.value)} />
              <input
                className="input"
                placeholder="值"
                aria-label="新增值"
                value={newValue}
                onChange={(e) => setNewValue(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault()
                    addEntry()
                  }
                }}
              />
              <button type="button" className="btn" onClick={addEntry} disabled={!newKey.trim()}>加入</button>
            </div>
          )}
        </>
      )}
      <button type="button" className="btn" onClick={() => setAdvanced((a) => !a)}>
        {advanced ? '切換為結構化編輯' : '切換為進階 JSON 模式'}
      </button>
    </div>
  )
}

/** 只接受 JSON object;陣列/純值一律當成形狀錯誤(後端也只收 object 形狀的 mapping)。 */
function parseInputMapping(text: string): { value: Record<string, unknown> | null; error: string | null } {
  if (!text.trim()) return { value: {}, error: null }
  try {
    const parsed: unknown = JSON.parse(text)
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return { value: null, error: 'Input mapping 必須是 JSON 物件。' }
    }
    return { value: parsed as Record<string, unknown>, error: null }
  } catch {
    return { value: null, error: 'Input mapping 不是合法 JSON。' }
  }
}

/**
 * 釘選的 input mapping + fire 歷史:歷史在展開時才載入(不預先為每一列打一支 API),
 * 鍵值攤平顯示。觸發結果(root run)與略過/失敗原因都只存在於 occurrence 的 bounded
 * status,所以一律在這裡呈現,列表層不再自行推測。
 */
function TriggerDetails({ trigger }: { trigger: Trigger }) {
  const triggerId = trigger.id
  const [items, setItems] = useState<TriggerOccurrence[] | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [cursor, setCursor] = useState<string | null>(null)
  const [hasMore, setHasMore] = useState(false)
  const mappingEntries = Object.entries(trigger.inputMapping)
  // 世代守衛：避免連點「重新載入」或與「載入更多」交錯時，較舊的回應晚到覆寫較新的畫面狀態
  // （與 ApprovalInbox 的 queueGenerationRef、RunsView 的 generationRef 同一個手法）。
  const generationRef = useRef(0)

  const load = useCallback(async (from: string | null) => {
    const generation = ++generationRef.current
    setLoading(true)
    setError(null)
    try {
      const page = await listTriggerOccurrences(triggerId, { cursor: from })
      if (generation !== generationRef.current) return
      setItems((prev) => (from && prev ? [...prev, ...page.items] : page.items))
      setCursor(page.cursor)
      setHasMore(page.hasMore)
    } catch (e) {
      if (generation === generationRef.current) setError((e as Error).message)
    } finally {
      if (generation === generationRef.current) setLoading(false)
    }
  }, [triggerId])

  return (
    <details
      onToggle={(e) => {
        if (e.currentTarget.open && items === null && !loading) void load(null)
      }}
    >
      <summary>展開 input mapping 與 fire 歷史</summary>
      <p className="muted">Input mapping</p>
      {mappingEntries.length === 0 ? (
        <p className="muted">未設定任何輸入欄位。</p>
      ) : (
        <dl className="agent-test-console__summary">
          {mappingEntries.map(([key, val]) => (
            <div key={key}>
              <dt>{key}</dt>
              <dd>{val !== null && typeof val === 'object' ? JSON.stringify(val) : String(val)}</dd>
            </div>
          ))}
        </dl>
      )}
      <p className="muted">Fire 歷史</p>
      {loading && items === null && <Skeleton rows={2} />}
      {error && (
        <div className="agent-errors" role="alert">
          載入失敗：{error}
          <button className="btn" type="button" disabled={loading} onClick={() => void load(null)}>重新載入</button>
        </div>
      )}
      {items !== null && items.length === 0 && !error && <p className="muted">尚未有觸發紀錄。</p>}
      {items?.map((occurrence) => (
        <dl key={occurrence.id} className="agent-test-console__summary">
          <div><dt>預定時間</dt><dd>{occurrence.scheduledFor ? fmtDate(occurrence.scheduledFor) : '—'}</dd></div>
          <div><dt>投遞狀態</dt><dd>{occurrenceStatusLabel(occurrence.status)}</dd></div>
          <div>
            <dt>觸發結果</dt>
            <dd>{occurrence.rootRunId ? `${shortId(occurrence.rootRunId)}(請至「執行總覽」查看)` : '—'}</dd>
          </div>
        </dl>
      ))}
      {hasMore && (
        <button className="btn" type="button" disabled={loading} onClick={() => void load(cursor)}>載入更多</button>
      )}
    </details>
  )
}

function targetLabel(trigger: Trigger, orchestrators: OrchestratorSummary[] | null): string {
  const match = orchestrators?.find((o) => o.id === trigger.orchestratorId)
  const name = match?.name ?? shortId(trigger.orchestratorId)
  return trigger.orchestratorRevision !== null ? `${name} · r${trigger.orchestratorRevision}` : name
}

/**
 * O5「排程觸發」(04-operations-trigger-plan.md §6):一次性 durable trigger 的管理視圖。
 * 閘門(agentTriggersEnabled && workflow.manage)由 AppShell 側欄與掛載共同把關。
 * 改時間 = 取消重建(one-shot 沒有 update-in-place),所以這裡只有建立/列表/取消/歷史。
 */
export default function TriggersView() {
  const triggers = useResource(listTriggers)
  const orchestrators = useResource(listOrchestrators)
  const toast = useToast()
  const confirm = useConfirm()

  const [name, setName] = useState('')
  const [orchestratorId, setOrchestratorId] = useState('')
  const [revision, setRevision] = useState('')
  const [fireAtLocal, setFireAtLocal] = useState('')
  const [mappingText, setMappingText] = useState('{}')
  const [fieldError, setFieldError] = useState<Record<string, string>>({})
  const [submitting, setSubmitting] = useState(false)

  const mapping = parseInputMapping(mappingText)
  const publishedOrchestrators = orchestrators.data?.filter((o) => o.published_revision !== null) ?? null

  async function submit() {
    const fireAt = localInputToUtcIso(fireAtLocal)
    const revisionNumber = Number(revision)
    const errors: Record<string, string> = {}
    if (!name.trim()) errors.name = '請輸入名稱。'
    if (!orchestratorId.trim()) errors.target = '請選擇目標 Orchestrator。'
    if (!Number.isSafeInteger(revisionNumber) || revisionNumber <= 0) errors.revision = '請填寫目標 revision(正整數)。'
    if (!fireAt) errors.fireAt = '請選擇有效的預定時間。'
    if (mapping.error) errors.mapping = mapping.error
    setFieldError(errors)
    if (Object.keys(errors).length > 0) return

    setSubmitting(true)
    await runWithToast(
      toast,
      () => createTrigger({
        name: name.trim(),
        orchestratorId: orchestratorId.trim(),
        orchestratorRevision: revisionNumber,
        fireAt: fireAt!,
        inputMapping: mapping.value ?? {},
      }),
      {
        success: '已建立排程觸發器。',
        onSuccess: async () => {
          setName('')
          setOrchestratorId('')
          setRevision('')
          setFireAtLocal('')
          setMappingText('{}')
          await triggers.reload()
        },
      },
    )
    setSubmitting(false)
  }

  async function cancel(trigger: Trigger) {
    const ok = await confirm(
      `確認取消排程觸發器「${trigger.name}」?取消後不會再觸發;已經建立的執行仍會照原設定跑完。`,
      { danger: true, confirmLabel: '確認取消' },
    )
    if (!ok) return
    await runWithToast(toast, () => cancelTrigger(trigger.id), {
      success: '已取消排程觸發器。',
      onSuccess: () => triggers.reload(),
    })
  }

  const rows = triggers.data ?? []

  return (
    <section className="agent-block" aria-busy={triggers.loading}>
      <h2>排程觸發</h2>
      <p className="muted">
        在指定時刻自動啟動一次已發布的協作流程。一次性排程沒有「修改時間」——要改時間請取消後重新建立。
      </p>

      <section className="agent-block">
        <h3 className="agent-block__title">建立排程觸發</h3>
        <div className="field">
          <label htmlFor="trigger-name">名稱</label>
          <input
            id="trigger-name"
            className="input"
            value={name}
            disabled={submitting}
            aria-invalid={!!fieldError.name}
            onChange={(e) => setName(e.target.value)}
          />
          <ErrorText msg={fieldError.name ?? null} id="trigger-name-error" />
        </div>

        <CatalogPicker
          id="trigger-target"
          label="目標 Orchestrator"
          hint="只能挑已發布的協作流程;清單載入失敗時可手動輸入 id,授權與存在性仍由伺服器把關。"
          value={orchestratorId}
          disabled={submitting}
          items={publishedOrchestrators}
          itemsError={orchestrators.error}
          itemsLoading={orchestrators.loading}
          optionValue={(o) => o.id}
          optionLabel={(o) => `${o.name} (r${o.published_revision})`}
          placeholder="Orchestrator id"
          invalid={!!fieldError.target}
          invalidHint={fieldError.target}
          onChange={(id) => {
            setOrchestratorId(id)
            const picked = publishedOrchestrators?.find((o) => o.id === id)
            if (picked?.published_revision != null) setRevision(String(picked.published_revision))
          }}
        />

        <div className="field-row">
          <div className="field">
            <label htmlFor="trigger-revision">目標 revision</label>
            <input
              id="trigger-revision"
              className="input"
              type="number"
              min={1}
              value={revision}
              disabled={submitting}
              aria-invalid={!!fieldError.revision}
              onChange={(e) => setRevision(e.target.value)}
            />
            <ErrorText msg={fieldError.revision ?? null} id="trigger-revision-error" />
          </div>
          <div className="field">
            <label htmlFor="trigger-fire-at">預定時間(本地時間)</label>
            <input
              id="trigger-fire-at"
              className="input"
              type="datetime-local"
              value={fireAtLocal}
              disabled={submitting}
              aria-invalid={!!fieldError.fireAt}
              onChange={(e) => setFireAtLocal(e.target.value)}
            />
            <ErrorText msg={fieldError.fireAt ?? null} id="trigger-fire-at-error" />
          </div>
        </div>

        <InputMappingEditor
          text={mappingText}
          parseError={mapping.error}
          disabled={submitting}
          onChange={setMappingText}
        />
        <ErrorText msg={fieldError.mapping ?? null} id="trigger-input-mapping-error" />

        <button className="btn btn--primary" type="button" disabled={submitting} onClick={() => void submit()}>
          建立觸發器
        </button>
      </section>

      {triggers.error && (
        <div className="agent-errors" role="alert">
          載入失敗：{triggers.error}
          <button className="btn" type="button" onClick={() => void triggers.reload()}>重新載入</button>
        </div>
      )}

      {triggers.loading && rows.length === 0 && !triggers.error ? <Skeleton rows={3} /> : null}

      {!triggers.loading && !triggers.error && rows.length === 0 && (
        <p className="muted">目前沒有排程觸發器。</p>
      )}

      {rows.map((trigger) => (
        <article key={trigger.id} className="agent-block">
          <div className="agent-test-console__actions">
            <span className="badge badge--user">{trigger.name}</span>
            <span className={`chip ${triggerStatusChipClass(trigger.status)}`}>{triggerStatusLabel(trigger.status)}</span>
            {trigger.status === 'scheduled' && (
              <button className="btn btn--danger" type="button" onClick={() => void cancel(trigger)}>取消</button>
            )}
          </div>
          <dl className="agent-test-console__summary">
            <div><dt>目標</dt><dd>{targetLabel(trigger, orchestrators.data)}</dd></div>
            <div><dt>預定時間</dt><dd>{trigger.fireAt ? fmtDate(trigger.fireAt) : '—'}</dd></div>
            <div><dt>建立者</dt><dd>{trigger.createdBy ?? '—'}</dd></div>
          </dl>
          <TriggerDetails trigger={trigger} />
        </article>
      ))}
    </section>
  )
}
