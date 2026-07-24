import { useState } from 'react'
import {
  activateConfigurationSet,
  createConfigurationSet,
  deleteConfigurationSet,
  getConfigurationSet,
  listConfigurationSets,
  updateConfigurationSet,
} from '../api/configurationSets'
import { ApiError } from '../api/http'
import { CONFIG_FIELDS, draftToValues, validateConfigValues } from '../nodeParams'
import { fmtDate } from '../format'
import { useResource } from '../hooks/useResource'
import ErrorText from './ErrorText'
import Skeleton from './Skeleton'
import { useConfirm } from './ConfirmDialog'
import { runWithToast, useToast } from './Toast'

/** 編輯中的組（id=null 代表新建）。draft 為各鍵的字串草稿（未填 = 不覆寫）。 */
type Editing = { id: string | null; name: string; draft: Record<string, string> }

/**
 * 工作流節點參數（Configuration Set，P4d）：清單（多組 + 唯一 active 徽章）→ 選定/新建 → 表單。
 * 表單欄位 = 七個開放鍵，依型別出 number/select 並帶範圍限制（設計 §9）。
 * ADMIN-only（側欄已擋 + 後端把關;UI 檢查僅 UX，SSR-P4-019）。
 */
export default function NodeParamsTab({ isAdmin }: { isAdmin: boolean }) {
  const toast = useToast()
  const confirm = useConfirm()
  const { data, loading, error: loadError, reload } = useResource(listConfigurationSets)
  const sets = data ?? []
  const [editing, setEditing] = useState<Editing | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  function startCreate() {
    setFieldErrors({})
    setFormError(null)
    setEditing({ id: null, name: '', draft: {} })
  }

  async function startEdit(id: string) {
    setFieldErrors({})
    setFormError(null)
    await runWithToast(toast, () => getConfigurationSet(id), {
      onSuccess: (full) => {
        const draft: Record<string, string> = {}
        for (const f of CONFIG_FIELDS) {
          const v = full.values?.[f.key]
          if (v !== undefined) draft[f.key] = String(v)
        }
        setEditing({ id, name: full.name, draft })
      },
    })
  }

  async function onActivate(id: string) {
    await runWithToast(toast, () => activateConfigurationSet(id), {
      success: '已啟用',
      onSuccess: reload,
    })
  }

  async function onDelete(id: string, name: string) {
    if (!(await confirm(`刪除參數組「${name}」？此動作無法復原。`, { danger: true, confirmLabel: '刪除' })))
      return
    await runWithToast(toast, () => deleteConfigurationSet(id), {
      success: '已刪除',
      onSuccess: () => {
        if (editing?.id === id) setEditing(null)
        return reload()
      },
    })
  }

  function setDraftField(key: string, value: string) {
    setEditing((e) => (e ? { ...e, draft: { ...e.draft, [key]: value } } : e))
    setFieldErrors((fe) => {
      if (!fe[key]) return fe
      const { [key]: _drop, ...rest } = fe
      return rest
    })
  }

  async function onSave() {
    if (!editing) return
    const name = editing.name.trim()
    if (!name) {
      setFormError('請先填參數組名稱。')
      return
    }
    const values = draftToValues(editing.draft)
    const clientErrors = validateConfigValues(values)
    if (Object.keys(clientErrors).length > 0) {
      setFieldErrors(clientErrors)
      return
    }
    setBusy(true)
    setFormError(null)
    setFieldErrors({})
    try {
      if (editing.id) await updateConfigurationSet(editing.id, name, values)
      else await createConfigurationSet(name, values)
      toast('已儲存', 'success')
      setEditing(null)
      await reload()
    } catch (e) {
      // 後端越界 → 422 + fieldErrors（鍵為 config key）;逐鍵顯示，其餘走頂部錯誤。
      if (e instanceof ApiError && e.fieldErrors) setFieldErrors(e.fieldErrors)
      setFormError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  // ---- 編輯/新建表單 ----
  if (editing) {
    return (
      <div className="node-params">
        <div className="skill-editor__head">
          <h3 className="skill-editor__title">
            {editing.id ? '編輯參數組' : '新增參數組'}
          </h3>
          <div className="skill-editor__actions">
            <button className="btn" type="button" onClick={() => setEditing(null)} disabled={busy}>
              關閉
            </button>
          </div>
        </div>

        <div className="field">
          <label htmlFor="cs-name">名稱</label>
          <input
            id="cs-name"
            className="input"
            value={editing.name}
            placeholder="例如：high-recall"
            onChange={(ev) => setEditing((e) => (e ? { ...e, name: ev.target.value } : e))}
          />
        </div>

        <p className="muted">未填的欄位沿用系統全域預設（顯示於提示）。</p>

        {CONFIG_FIELDS.map((f) => (
          <div className="field" key={f.key}>
            <label htmlFor={`cs-${f.key}`}>
              {f.label} <span className="muted">（{f.key}）</span>
            </label>
            {f.kind === 'select' ? (
              <select
                id={`cs-${f.key}`}
                className="input"
                value={editing.draft[f.key] ?? ''}
                onChange={(ev) => setDraftField(f.key, ev.target.value)}
              >
                <option value="">預設（{f.default}）</option>
                {f.options!.map((o) => (
                  <option key={o} value={o}>
                    {o}
                  </option>
                ))}
              </select>
            ) : (
              <input
                id={`cs-${f.key}`}
                className="input"
                type="number"
                min={f.min}
                max={f.max}
                step={f.step ?? (f.kind === 'int' ? 1 : 'any')}
                value={editing.draft[f.key] ?? ''}
                placeholder={`預設 ${f.default}${rangeHint(f.min, f.max)}`}
                onChange={(ev) => setDraftField(f.key, ev.target.value)}
              />
            )}
            {fieldErrors[f.key] && (
              <p className="field-error" role="alert">
                {fieldErrors[f.key]}
              </p>
            )}
          </div>
        ))}

        <ErrorText msg={formError} />

        <div className="skills__bar">
          <button className="btn btn--primary" type="button" onClick={onSave} disabled={busy}>
            {busy ? '儲存中…' : '儲存'}
          </button>
        </div>
      </div>
    )
  }

  // ---- 清單 ----
  return (
    <div className="node-params">
      {isAdmin && (
        <div className="skills__bar">
          <button className="btn btn--info" onClick={startCreate}>
            ＋ 新增參數組
          </button>
        </div>
      )}

      <ErrorText msg={loadError} />

      {loading && sets.length === 0 ? (
        <Skeleton rows={3} />
      ) : sets.length === 0 && !loadError ? (
        <p className="muted">尚無參數組。新增一組並啟用，即可覆寫系統全域預設。</p>
      ) : (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>名稱</th>
                <th>狀態</th>
                <th>更新時間</th>
                {isAdmin && <th>操作</th>}
              </tr>
            </thead>
            <tbody>
              {sets.map((s) => (
                <tr key={s.id}>
                  <td>{s.name}</td>
                  <td>
                    {s.is_active && <span className="badge badge--src-builtin">使用中</span>}
                  </td>
                  <td className="muted">{fmtDate(s.updated_at)}</td>
                  {isAdmin && (
                    <td className="skills__ops">
                      <button className="btn" onClick={() => startEdit(s.id)}>
                        編輯
                      </button>
                      <button
                        className="btn"
                        onClick={() => onActivate(s.id)}
                        disabled={s.is_active}
                      >
                        啟用
                      </button>
                      <button className="btn btn--danger" onClick={() => onDelete(s.id, s.name)}>
                        刪除
                      </button>
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

function rangeHint(min?: number, max?: number): string {
  if (min !== undefined && max !== undefined) return `，範圍 ${min}–${max}`
  if (min !== undefined) return `，最小 ${min}`
  return ''
}
