import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError } from '../api/http'
import {
  createAgent,
  getAgent,
  listAgentToolCatalog,
  listAgentRevisions,
  publishAgent,
  putAgentDraft,
  restoreAgentRevision,
  validateAgent,
} from '../api/agents'
import { listSkillCatalog } from '../api/skills'
import type {
  AgentDraft,
  AgentExecutionRole,
  AgentRuntimeLimits,
  AgentToolCatalogEntry,
  AgentValidation,
  SkillCatalogEntry,
} from '../types'
import {
  businessRuleCount,
  createEmptyAgentDraft,
  isAgentEditorLocked,
  isSkillBindable,
  nextAgentRevision,
  normalizeAgentDraft,
  parseOutputContract,
} from '../agentBuilder'
import { fmtDate } from '../format'
import { useResource } from '../hooks/useResource'
import ErrorText from './ErrorText'
import Skeleton from './Skeleton'
import { useConfirm } from './ConfirmDialog'
import { runWithToast, useToast } from './Toast'
import BusinessRuleEditor from './BusinessRuleEditor'

interface Props {
  /** null = 建立模式；有值 = 編輯既有 Agent。 */
  agentId: string | null
  isAdmin: boolean
  onClose: () => void
  /** 建立成功後把新 id 交回,由 AgentsView 切換到編輯模式。 */
  onCreated: (id: string) => void
  /** 任何會改變清單的操作(存草稿/發布/停用/回溯)後通知父層重載清單。 */
  onChanged?: () => void
}

const ROLES: { id: AgentExecutionRole; label: string }[] = [
  { id: 'worker', label: 'Worker' },
  { id: 'verifier', label: 'Verifier' },
]

/** 409/412 都是樂觀併發衝突:草稿已被他人更新或發布內容與已驗證版本不符,需重新載入。 */
function isConflict(e: unknown): boolean {
  return e instanceof ApiError && (e.status === 409 || e.status === 412)
}

/** 建立時 slug 撞名 → 409。其餘沿用後端 message。 */
function createErrorMessage(e: unknown): string {
  if (e instanceof ApiError && e.status === 409) return 'slug 已被使用,請換一個。'
  return (e as Error).message
}

/** 順序無關的草稿指紋:用來判斷表單相對上次存檔是否有變(dirty)。 */
function draftKey(d: AgentDraft): string {
  return JSON.stringify({
    name: d.name,
    slug: d.slug,
    description: d.description,
    system_prompt: d.system_prompt,
    execution_roles: [...d.execution_roles].sort(),
    capabilities: [...d.capabilities].sort(),
    output_contract: d.output_contract,
    audience: [...d.audience].sort(),
    allowed_tools: [...d.allowed_tools].sort(),
    knowledge_sources: [...d.knowledge_sources].sort(),
    skill_bindings: d.skill_bindings.map((b) => `${b.skill}:${b.revision_policy ?? ''}`).sort(),
    business_rules: d.business_rules,
    runtime_limits: d.runtime_limits,
    runtime_workflow: d.runtime_workflow,
  })
}

/** 可增刪的字串集合欄位(工具 allowlist / 知識來源)。空集合明確呈現為「無權限」。 */
function StringSetEditor({
  id,
  label,
  hint,
  items,
  placeholder,
  disabled,
  onChange,
}: {
  id: string
  label: string
  hint: string
  items: string[]
  placeholder: string
  disabled: boolean
  onChange: (next: string[]) => void
}) {
  const [text, setText] = useState('')

  function add() {
    const v = text.trim()
    if (!v || items.includes(v)) {
      setText('')
      return
    }
    onChange([...items, v])
    setText('')
  }

  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <p className="muted agent-set__hint">{hint}</p>
      {items.length === 0 ? (
        <p className="agent-set__empty" role="note">
          無權限(空集合)—— 未加入任何項目即代表沒有授權,不是全部開放。
        </p>
      ) : (
        <ul className="agent-set__chips">
          {items.map((it) => (
            <li key={it} className="agent-set__chip">
              <span className="agent-set__chip-label">{it}</span>
              {!disabled && (
                <button
                  type="button"
                  className="agent-set__chip-x"
                  aria-label={`移除 ${it}`}
                  onClick={() => onChange(items.filter((x) => x !== it))}
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
          <input
            id={id}
            className="input"
            value={text}
            placeholder={placeholder}
            onChange={(e) => setText(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault()
                add()
              }
            }}
          />
          <button type="button" className="btn" onClick={add} disabled={!text.trim()}>
            加入
          </button>
        </div>
      )}
    </div>
  )
}

/**
 * Agent Builder 編輯器(建立精靈 + 草稿編輯合一)。負責:身分/System Prompt/工具/知識來源/
 * Skill 綁定表單、ETag 樂觀併發(412 提示重載)、validate(field_errors 定位欄位)、
 * 發布預覽(顯示將被固定的 skill revisions)→ publish、版本歷史 + 回溯。寫入操作僅 ADMIN。
 */
export default function AgentEditor({ agentId, isAdmin, onClose, onCreated, onChanged }: Props) {
  const toast = useToast()
  const confirm = useConfirm()
  const creating = !agentId
  const readOnly = !isAdmin

  const [form, setForm] = useState<AgentDraft>(() => createEmptyAgentDraft())
  const [savedDraft, setSavedDraft] = useState<AgentDraft | null>(null)
  const [outputContractText, setOutputContractText] = useState('{}')
  const [outputContractError, setOutputContractError] = useState<string | null>(null)
  const [etag, setEtag] = useState<string | null>(null)
  const [draftVersion, setDraftVersion] = useState(0)
  const [publishedRevision, setPublishedRevision] = useState<number | null>(null)
  const [enabled, setEnabled] = useState(true)
  const [loading, setLoading] = useState(!!agentId)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [validation, setValidation] = useState<AgentValidation | null>(null)
  const [validatedVersion, setValidatedVersion] = useState<number | null>(null)
  const [conflict, setConflict] = useState(false)
  const [sub, setSub] = useState<'edit' | 'history'>('edit')
  const [showPreview, setShowPreview] = useState(false)
  const previewTriggerRef = useRef<HTMLButtonElement | null>(null)
  const previewCancelRef = useRef<HTMLButtonElement | null>(null)
  const previewConfirmRef = useRef<HTMLButtonElement | null>(null)
  const restorePreviewFocusRef = useRef(false)

  const catalogRes = useResource(listSkillCatalog)
  const catalog: SkillCatalogEntry[] = (catalogRes.data ?? []).filter(
    (c) => !c.name.startsWith('template-'),
  )
  const bindableSkills = catalog.filter(isSkillBindable)
  const nonBindableSkills = catalog.filter((skill) => !isSkillBindable(skill))
  const toolCatalogRes = useResource(listAgentToolCatalog)
  const toolCatalog: AgentToolCatalogEntry[] = toolCatalogRes.data ?? []

  const fetchRevisions = useCallback(
    () => (agentId ? listAgentRevisions(agentId) : Promise.resolve([])),
    [agentId],
  )
  const revisions = useResource(fetchRevisions)

  const fetchAgent = useCallback(
    async (spinner: boolean) => {
      if (!agentId) return false
      if (spinner) setLoading(true)
      setLoadError(null)
      try {
        const { data, etag: tag } = await getAgent(agentId)
        const normalized = normalizeAgentDraft(data.draft, {
          name: data.name,
          slug: data.slug,
          description: data.description,
        })
        setForm(normalized)
        setSavedDraft(normalized)
        setOutputContractText(JSON.stringify(normalized.output_contract, null, 2))
        setOutputContractError(null)
        setEtag(tag)
        setDraftVersion(data.draft_version)
        setPublishedRevision(data.published_revision)
        setEnabled(data.enabled)
        setValidation(null)
        setValidatedVersion(null)
        setConflict(false)
        return true
      } catch (e) {
        setLoadError((e as Error).message)
        return false
      } finally {
        if (spinner) setLoading(false)
      }
    },
    [agentId],
  )

  useEffect(() => {
    void fetchAgent(true)
  }, [fetchAgent])

  const closePreview = useCallback(() => {
    restorePreviewFocusRef.current = true
    setShowPreview(false)
  }, [])

  useEffect(() => {
    if (!showPreview && !busy && restorePreviewFocusRef.current) {
      restorePreviewFocusRef.current = false
      previewTriggerRef.current?.focus()
    }
  }, [busy, showPreview])

  useEffect(() => {
    if (!showPreview) return
    previewCancelRef.current?.focus()
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !busy) {
        event.preventDefault()
        closePreview()
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [busy, closePreview, showPreview])

  // 任何欄位編輯都讓上次的 validation 失效(規格 §2.2:draft 再修改後必須重新驗證)。
  function patch(p: Partial<AgentDraft>) {
    setForm((f) => ({ ...f, ...p }))
    setValidation(null)
  }

  function patchOutputContract(text: string) {
    setOutputContractText(text)
    const parsed = parseOutputContract(text)
    setOutputContractError(parsed.error)
    setValidation(null)
    if (parsed.value) setForm((current) => ({ ...current, output_contract: parsed.value! }))
  }

  function toggleRole(role: AgentExecutionRole) {
    patch({
      execution_roles: form.execution_roles.includes(role)
        ? form.execution_roles.filter((r) => r !== role)
        : [...form.execution_roles, role],
    })
  }

  function toggleBinding(name: string) {
    const has = form.skill_bindings.some((b) => b.skill === name)
    patch({
      skill_bindings: has
        ? form.skill_bindings.filter((b) => b.skill !== name)
        : [...form.skill_bindings, { skill: name }],
    })
  }

  function toggleTool(name: string) {
    patch({
      allowed_tools: form.allowed_tools.includes(name)
        ? form.allowed_tools.filter((tool) => tool !== name)
        : [...form.allowed_tools, name],
    })
  }

  function patchRuntimeLimit(key: keyof AgentRuntimeLimits, value: string) {
    const parsed = Number(value)
    patch({
      runtime_limits: {
        ...form.runtime_limits,
        [key]: Number.isFinite(parsed) && parsed >= 0 ? parsed : 0,
      },
    })
  }

  const dirty = (savedDraft ? draftKey(form) !== draftKey(savedDraft) : true) || !!outputContractError

  // 發布預覽:每個 binding 於發布時將被固定到該 Skill 目前 revision(從 catalog 解析)。
  const preview = form.skill_bindings.map((b) => {
    const entry = bindableSkills.find((c) => c.name === b.skill)
    return {
      skill: b.skill,
      pinned_revision: entry?.revision ?? null,
      missing: !!catalogRes.error || (!!catalogRes.data && !entry),
    }
  })
  const hasInvalidBinding = preview.some((p) => p.missing)
  const missingTools = toolCatalogRes.error
    ? [...form.allowed_tools]
    : toolCatalogRes.data
      ? form.allowed_tools.filter((name) => !toolCatalog.some((tool) => tool.name === name))
      : []
  const hasUnverifiedBindings = form.skill_bindings.length > 0 && !catalogRes.data
  const hasUnverifiedTools = form.allowed_tools.length > 0 && !toolCatalogRes.data
  const selectedTools = form.allowed_tools.flatMap((name) => {
    const tool = toolCatalog.find((entry) => entry.name === name)
    return tool ? [tool] : []
  })
  const rulesCount = businessRuleCount(form.business_rules)
  const outputContractFields = Object.keys(form.output_contract)
  const hasConcurrencyToken = creating || !!etag
  const locked = isAgentEditorLocked(readOnly, busy, conflict)

  const validatedForCurrent = !!validation && validatedVersion === draftVersion && !dirty
  const canPublish =
    isAdmin &&
    enabled &&
    !conflict &&
    hasConcurrencyToken &&
    validatedForCurrent &&
    validation?.valid === true &&
    !loadError &&
    !hasInvalidBinding &&
    !hasUnverifiedBindings &&
    missingTools.length === 0 &&
    !hasUnverifiedTools &&
    !outputContractError

  function fieldError(key: string): string | undefined {
    if (!validatedForCurrent) return undefined
    return validation?.errors.find((e) => e.field === key)?.message
  }

  // ---- 建立模式:只有表單 + 建立鈕(建立後由父層切換到編輯模式載入完整功能) ----
  async function onCreate() {
    if (outputContractError) return
    setBusy(true)
    try {
      const created = await createAgent(form)
      toast('已建立 Agent', 'success')
      onChanged?.()
      onCreated(created.id)
    } catch (e) {
      toast(createErrorMessage(e), 'error')
    } finally {
      setBusy(false)
    }
  }

  async function onSaveDraft() {
    if (!agentId || conflict || !etag || outputContractError) return
    setBusy(true)
    try {
      await putAgentDraft(agentId, form, etag)
      await fetchAgent(false) // 重讀取新 ETag/draft_version;dirty 歸零、validation 已清
      toast('已儲存草稿', 'success')
      onChanged?.()
    } catch (e) {
      if (isConflict(e)) setConflict(true)
      else toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  async function onValidate() {
    if (!agentId || conflict || !etag || dirty) return
    setBusy(true)
    try {
      const v = await validateAgent(agentId, etag)
      if (!(await fetchAgent(false))) {
        toast('規則已在伺服器驗證，但無法重載 canonical 草稿；請重新整理後再發布。', 'error')
        return
      }
      setValidation(v)
      setValidatedVersion(draftVersion)
      toast(v.valid ? '驗證通過' : '驗證發現問題,請依欄位提示修正。', v.valid ? 'success' : 'error')
    } catch (e) {
      if (isConflict(e)) setConflict(true)
      else toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  async function onPublish() {
    if (!agentId || !canPublish) return
    setBusy(true)
    try {
      await publishAgent(agentId, draftVersion, etag)
      closePreview()
      toast('已發布', 'success')
      await fetchAgent(false)
      await revisions.reload()
      onChanged?.()
    } catch (e) {
      if (isConflict(e)) {
        closePreview()
        setConflict(true)
      } else {
        toast((e as Error).message, 'error')
      }
    } finally {
      setBusy(false)
    }
  }

  async function onRestore(revision: number) {
    if (!agentId) return
    if (
      !(await confirm(
        `回溯到 r${revision}?將以該版內容重新發布為一個新的 revision,歷史不會被改寫。`,
        { confirmLabel: '回溯' },
      ))
    )
      return
    setBusy(true)
    try {
      await runWithToast(toast, () => restoreAgentRevision(agentId, revision), {
        success: '已回溯(產生新版)',
        onSuccess: async () => {
          await fetchAgent(false)
          await revisions.reload()
          onChanged?.()
        },
      })
    } finally {
      setBusy(false)
    }
  }

  if (loading) {
    return (
      <div className="agent-editor">
        <Skeleton rows={5} />
      </div>
    )
  }

  const title = creating ? '建立 Agent' : form.name || '(未命名 Agent)'

  // 已內聯到欄位的錯誤不再彙總;其餘(含無 field 的整體錯誤)列在下方摘要。
  const inlineKeys = new Set([
    'name',
    'slug',
    'description',
    'system_prompt',
    'output_contract',
    'skill_bindings',
  ])
  const otherErrors = validatedForCurrent
    ? validation!.errors
        .filter((e) => !e.field || !inlineKeys.has(e.field))
        .map((e) => (e.field ? `${e.field}: ${e.message}` : e.message))
    : []

  return (
    <div className="agent-editor">
      <div className="skill-editor__head">
        <button className="btn" type="button" onClick={onClose} disabled={busy}>
          ← 返回清單
        </button>
        <h3 className="skill-editor__title">
          {title}
          {!creating && (
            <>
              <span className={`badge badge--${enabled ? 'user' : 'admin'}`}>
                {enabled ? '啟用中' : '已停用'}
              </span>
              <span className="badge badge--user">
                {publishedRevision != null ? `已發布 r${publishedRevision}` : '未發布'}
              </span>
            </>
          )}
        </h3>
        {!creating && (
          <div className="seg" role="group" aria-label="Agent 子功能">
            <button className="btn" aria-pressed={sub === 'edit'} onClick={() => setSub('edit')}>
              編輯
            </button>
            <button className="btn" aria-pressed={sub === 'history'} onClick={() => setSub('history')}>
              版本控管
            </button>
          </div>
        )}
      </div>

      {conflict && (
        <div className="agent-conflict" role="alert">
          <span>此 Agent 已被其他人更新,你的變更未套用。請重新載入後再編輯。</span>
          <button className="btn" type="button" onClick={() => fetchAgent(true)} disabled={busy}>
            重新載入
          </button>
        </div>
      )}

      <ErrorText msg={loadError} />

      {readOnly && (
        <p className="muted" role="note">
          你的角色只能檢視 Agent 設定,無法建立、修改或發布(伺服器為真正的授權邊界)。
        </p>
      )}

      {sub === 'history' && !creating ? (
        <section className="skill-history">
          <p className="muted">唯讀歷史(依 revision 遞減);回溯會以該版重新發布為新 revision。</p>
          <ErrorText msg={revisions.error} />
          {!revisions.data && !revisions.error ? (
            <Skeleton rows={3} />
          ) : revisions.data && revisions.data.length === 0 ? (
            <p className="muted">尚無 revision(此 Agent 未曾發布)。</p>
          ) : (
            revisions.data?.map((r, i) => (
              <details className="rev" key={r.revision} open={i === 0}>
                <summary className="rev__head">
                  <span className="badge badge--user">r{r.revision}</span>
                  <span className="rev__meta">
                    {r.created_by} · {fmtDate(r.created_at)}
                  </span>
                  <code className="rev__sha">{r.definition_sha256.slice(0, 12)}</code>
                  {isAdmin && i !== 0 && (
                    <button
                      className="btn"
                      type="button"
                      disabled={busy || conflict}
                      onClick={(e) => {
                        e.preventDefault()
                        void onRestore(r.revision)
                      }}
                    >
                      回溯此版
                    </button>
                  )}
                </summary>
                <ul className="agent-rev__bindings">
                  {r.skill_bindings.length === 0 ? (
                    <li className="muted">無 Skill 綁定</li>
                  ) : (
                    r.skill_bindings.map((b) => (
                      <li key={b.skill}>
                        {b.skill} → r{b.skill_revision}
                        {b.enabled ? '' : '(已停用)'}
                      </li>
                    ))
                  )}
                </ul>
              </details>
            ))
          )}
        </section>
      ) : (
        <>
          {/* ── 身分 ── */}
          <section className="agent-block">
            <h4 className="agent-block__title">身分</h4>
            <div className="field">
              <label htmlFor="agent-name">名稱</label>
              <input
                id="agent-name"
                className="input"
                value={form.name}
                disabled={locked}
                aria-invalid={!!fieldError('name')}
                placeholder="租戶內顯示名稱"
                onChange={(e) => patch({ name: e.target.value })}
              />
              {fieldError('name') && (
                <span className="field-error" role="alert">
                  {fieldError('name')}
                </span>
              )}
            </div>
            <div className="field">
              <label htmlFor="agent-slug">slug</label>
              <input
                id="agent-slug"
                className="input"
                value={form.slug}
                disabled={locked || !creating}
                aria-invalid={!!fieldError('slug')}
                placeholder="租戶內唯一、穩定的 API 識別字"
                onChange={(e) => patch({ slug: e.target.value })}
              />
              {!creating && <p className="muted">slug 是穩定識別字,建立後不可變更。</p>}
              {fieldError('slug') && (
                <span className="field-error" role="alert">
                  {fieldError('slug')}
                </span>
              )}
            </div>
            <div className="field">
              <label htmlFor="agent-desc">描述</label>
              <input
                id="agent-desc"
                className="input"
                value={form.description}
                disabled={locked}
                aria-invalid={!!fieldError('description')}
                placeholder="用途與適用情境"
                onChange={(e) => patch({ description: e.target.value })}
              />
              {fieldError('description') && (
                <span className="field-error" role="alert">
                  {fieldError('description')}
                </span>
              )}
            </div>
          </section>

          {/* ── System Prompt ── */}
          <section className="agent-block">
            <h4 className="agent-block__title">System Prompt</h4>
            <div className="field">
              <label htmlFor="agent-system-prompt">角色與行為指引</label>
              <textarea
                id="agent-system-prompt"
                className="textarea"
                value={form.system_prompt}
                disabled={locked}
                aria-invalid={!!fieldError('system_prompt')}
                placeholder="角色、目標、語氣、一般行為指引"
                onChange={(e) => patch({ system_prompt: e.target.value })}
              />
              {fieldError('system_prompt') && (
                <span className="field-error" role="alert">
                  {fieldError('system_prompt')}
                </span>
              )}
            </div>
          </section>

          {/* ── 執行角色 ── */}
          <section className="agent-block">
            <h4 className="agent-block__title">執行角色</h4>
            <div className="agent-roles">
              {ROLES.map((r) => (
                <label key={r.id} className="agent-check">
                  <input
                    type="checkbox"
                    checked={form.execution_roles.includes(r.id)}
                    disabled={locked}
                    onChange={() => toggleRole(r.id)}
                  />
                  {r.label}
                </label>
              ))}
            </div>
          </section>

          {/* ── Discovery / audience / output contract ── */}
          <section className="agent-block">
            <h4 className="agent-block__title">能力與使用範圍</h4>
            <StringSetEditor
              id="agent-capabilities"
              label="Capabilities"
              hint="供 Orchestrator discovery/selection 使用的 typed tags；空集合代表不會被能力條件選中。"
              items={form.capabilities}
              placeholder="例如 research、analysis"
              disabled={locked}
              onChange={(next) => patch({ capabilities: next })}
            />
            <StringSetEditor
              id="agent-audience"
              label="Audience"
              hint="可使用此 Agent 的角色或群組；空集合會 fail-closed，任何人都不可啟動。"
              items={form.audience}
              placeholder="例如 USER、finance-reviewers"
              disabled={locked}
              onChange={(next) => patch({ audience: next })}
            />
            <div className="field">
              <label htmlFor="agent-output-contract">Output contract（JSON object）</label>
              <textarea
                id="agent-output-contract"
                className="textarea code-textarea"
                value={outputContractText}
                disabled={locked}
                aria-invalid={!!outputContractError}
                aria-describedby={outputContractError ? 'agent-output-contract-error' : undefined}
                onChange={(event) => patchOutputContract(event.target.value)}
              />
              {outputContractError && (
                <span id="agent-output-contract-error" className="field-error" role="alert">
                  {outputContractError}
                </span>
              )}
              {!outputContractError && fieldError('output_contract') && (
                <span className="field-error" role="alert">
                  {fieldError('output_contract')}
                </span>
              )}
            </div>
          </section>

          {/* ── 工具 allowlist ── */}
          <section className="agent-block">
            <h4 className="agent-block__title">工具 allowlist</h4>
            <p className="muted">
              只可從 server Tool Catalog 選取；目錄不包含 endpoint/token。空集合代表不授權任何工具。
            </p>
            <ErrorText msg={toolCatalogRes.error} />
            {missingTools.map((name) => (
              <p key={name} className="field-error" role="alert">
                已允許的工具「{name}」不在目前 Tool Catalog，請移除後才能發布。
                {!locked && (
                  <button type="button" className="btn" onClick={() => toggleTool(name)}>
                    移除
                  </button>
                )}
              </p>
            ))}
            {!toolCatalogRes.data && !toolCatalogRes.error ? (
              <Skeleton rows={3} />
            ) : toolCatalog.length === 0 ? (
              <p className="agent-set__empty" role="note">
                Tool Catalog 目前沒有可選工具；此 Agent 將保持無工具權限。
              </p>
            ) : (
              <ul className="agent-skills">
                {toolCatalog.map((tool) => (
                  <li key={tool.name} className="agent-skills__row">
                    <label className="agent-check">
                      <input
                        type="checkbox"
                        checked={form.allowed_tools.includes(tool.name)}
                        disabled={locked}
                        onChange={() => toggleTool(tool.name)}
                      />
                      <span className="agent-skills__name">{tool.name}</span>
                    </label>
                    <span className="muted agent-skills__desc">{tool.description}</span>
                    <span className="badge badge--user">{tool.kind}</span>
                    <span
                      className={`badge badge--${
                        tool.risk === 'write' || tool.risk === 'privileged' ? 'admin' : 'user'
                      }`}
                      title={`回傳：${tool.returns}`}
                    >
                      風險：{tool.risk}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </section>

          {/* ── 知識來源 ── */}
          <section className="agent-block">
            <h4 className="agent-block__title">知識來源</h4>
            <StringSetEditor
              id="agent-knowledge-sources"
              label="knowledge_sources"
              hint="可用的知識來源範圍;空集合 = 不授權任何知識來源。"
              items={form.knowledge_sources}
              placeholder="知識來源識別字"
              disabled={locked}
              onChange={(next) => patch({ knowledge_sources: next })}
            />
          </section>

          {/* ── Skill 綁定 ── */}
          <section className="agent-block">
            <h4 className="agent-block__title">Skill 綁定</h4>
            <p className="muted">
              只顯示同租戶、可執行的 Skill;發布時會把每個綁定固定到當時的確切 revision。
            </p>
            <ErrorText msg={catalogRes.error} />
            {/* 已綁定但 catalog 已無(停用/移除)的 Skill:定位為失效,禁止發布(A-UI-05)。 */}
            {preview
              .filter((p) => p.missing)
              .map((p) => (
                <p key={p.skill} className="field-error" role="alert">
                  已綁定的 Skill「{p.skill}」已失效、停用或不可固定 revision，請移除或改綁其他
                  Skill 後才能發布。
                  {!locked && (
                    <button
                      type="button"
                      className="btn"
                      onClick={() => toggleBinding(p.skill)}
                    >
                      移除
                    </button>
                  )}
                </p>
              ))}
            {!catalogRes.data && !catalogRes.error ? (
              <Skeleton rows={3} />
            ) : (
              <ul className="agent-skills">
                {catalog.map((c) => {
                  const bound = form.skill_bindings.some((b) => b.skill === c.name)
                  const bindable = isSkillBindable(c)
                  return (
                    <li key={c.name} className="agent-skills__row">
                      <label className="agent-check">
                        <input
                          type="checkbox"
                          checked={bound}
                          disabled={locked || !bindable}
                          onChange={() => toggleBinding(c.name)}
                        />
                        <span className="agent-skills__name">{c.name}</span>
                      </label>
                      <span className="muted agent-skills__desc">{c.description}</span>
                      <span className={`badge badge--${bindable ? 'user' : 'admin'}`}>
                        {bindable
                          ? c.revision != null
                            ? `可綁 · r${c.revision}`
                            : '可綁'
                          : '不可綁 · 無持久 revision'}
                      </span>
                    </li>
                  )
                })}
              </ul>
            )}
            {nonBindableSkills.length > 0 && (
              <p className="muted" role="note">
                內建 Skill 可直接執行，但目前沒有可固定的 persisted immutable revision，因此不能綁入
                Agent。請先上傳為 tenant Skill。
              </p>
            )}
            {fieldError('skill_bindings') && (
              <span className="field-error" role="alert">
                {fieldError('skill_bindings')}
              </span>
            )}
          </section>

          <BusinessRuleEditor
            value={form.business_rules}
            disabled={locked}
            onChange={(business_rules) => patch({ business_rules })}
          />

          {/* ── Runtime limits / Harness pin ── */}
          <section className="agent-block">
            <h4 className="agent-block__title">Runtime limits</h4>
            <p className="muted">
              0 代表尚未配置；正式 Runtime 會依 Harness 與政策交集 fail-closed，不會解讀成無上限。
            </p>
            <div className="agent-runtime-grid">
              {(
                [
                  ['max_tool_rounds', '工具輪數'],
                  ['max_context_rounds', 'Context 輪數'],
                  ['timeout_seconds', '逾時秒數'],
                  ['token_budget', 'Token budget'],
                  ['step_budget', 'Step budget'],
                ] as const
              ).map(([key, label]) => (
                <div className="field" key={key}>
                  <label htmlFor={`agent-runtime-${key}`}>{label}</label>
                  <input
                    id={`agent-runtime-${key}`}
                    className="input"
                    type="number"
                    min={0}
                    step={1}
                    value={form.runtime_limits[key]}
                    disabled={locked}
                    onChange={(event) => patchRuntimeLimit(key, event.target.value)}
                  />
                </div>
              ))}
            </div>
            <p className="muted">
              Execution Harness：
              {form.runtime_workflow
                ? `${form.runtime_workflow.id} · r${form.runtime_workflow.revision}`
                : '建立／發布時由 server 固定到 Default Agent-Runtime Workflow'}
            </p>
          </section>

          {/* ── 驗證結果(非欄位級) ── */}
          {validatedForCurrent && (validation!.valid ? otherErrors.length === 0 : true) && (
            <div
              className={validation!.valid ? 'notice-text' : 'agent-errors'}
              role={validation!.valid ? 'status' : 'alert'}
            >
              {validation!.valid ? (
                <span>驗證通過,可以發布。</span>
              ) : (
                <>
                  <p>驗證發現問題,請修正後重新驗證:</p>
                  {otherErrors.length > 0 && (
                    <ul>
                      {otherErrors.map((m) => (
                        <li key={m}>{m}</li>
                      ))}
                    </ul>
                  )}
                </>
              )}
            </div>
          )}

          {/* ── 動作列(僅 ADMIN) ── */}
          {isAdmin && (
            <div className="agent-actions">
              {creating ? (
                <button
                  className="btn btn--primary"
                  type="button"
                  onClick={() => void onCreate()}
                  disabled={busy || !!outputContractError || !form.name.trim() || !form.slug.trim()}
                >
                  {busy ? '建立中…' : '建立 Agent'}
                </button>
              ) : (
                <>
                  <button
                    className="btn btn--primary"
                    type="button"
                    onClick={() => void onSaveDraft()}
                    disabled={busy || conflict || !etag || !!outputContractError || !dirty}
                  >
                    {busy ? '儲存中…' : '儲存草稿'}
                  </button>
                  <button
                    className="btn"
                    type="button"
                    onClick={() => void onValidate()}
                    disabled={busy || conflict || !etag || dirty}
                    title={
                      conflict
                        ? '請先重新載入最新版本'
                        : dirty
                          ? '請先儲存草稿再驗證'
                          : !etag
                            ? '缺少 ETag，請重新載入'
                            : undefined
                    }
                  >
                    驗證
                  </button>
                  <button
                    className="btn btn--info"
                    type="button"
                    ref={previewTriggerRef}
                    onClick={() => setShowPreview(true)}
                    disabled={busy || !canPublish}
                    title={!validatedForCurrent ? '請先儲存並通過驗證' : undefined}
                  >
                    發布預覽
                  </button>
                </>
              )}
            </div>
          )}
          {isAdmin && !creating && dirty && (
            <p className="muted">草稿有未儲存的變更;驗證與發布需先儲存並重新驗證。</p>
          )}
          {isAdmin && !creating && !etag && !loadError && (
            <p className="field-error" role="alert">
              回應缺少 ETag，為避免覆蓋他人更新，寫入功能已鎖定；請重新載入。
            </p>
          )}
        </>
      )}

      {/* ── 發布預覽：完整顯示 immutable revision 的權限、規則、Skill 與 runtime 摘要。 ── */}
      {showPreview && (
        <div className="confirm-overlay" onClick={() => !busy && closePreview()}>
          <div
            className="confirm-dialog agent-preview"
            role="dialog"
            aria-modal="true"
            aria-labelledby="agent-preview-title"
            onClick={(e) => e.stopPropagation()}
            onKeyDown={(event) => {
              if (event.key !== 'Tab') return
              event.preventDefault()
              const next =
                document.activeElement === previewCancelRef.current
                  ? previewConfirmRef
                  : previewCancelRef
              next.current?.focus()
            }}
          >
            <h4 id="agent-preview-title" className="agent-block__title">
              發布預覽
            </h4>
            <p className="muted">
              發布會建立不可變的新 Agent revision；下列設定會成為本次快照。
            </p>

            <dl className="agent-preview__summary">
              <div>
                <dt>Agent revision</dt>
                <dd>r{nextAgentRevision(publishedRevision)}</dd>
              </div>
              <div>
                <dt>Execution roles</dt>
                <dd>{form.execution_roles.join('、') || '無'}</dd>
              </div>
              <div>
                <dt>Capabilities</dt>
                <dd>{form.capabilities.join('、') || '無（不接受 capability discovery）'}</dd>
              </div>
              <div>
                <dt>Audience</dt>
                <dd>{form.audience.join('、') || '無（任何人都不可啟動）'}</dd>
              </div>
              <div>
                <dt>Output contract</dt>
                <dd>{outputContractFields.join('、') || '空 object'}</dd>
              </div>
              <div>
                <dt>Business rules</dt>
                <dd>{rulesCount} 條（Phase 2 公式編輯器管理）</dd>
              </div>
              <div>
                <dt>Knowledge sources</dt>
                <dd>{form.knowledge_sources.join('、') || '無'}</dd>
              </div>
            </dl>

            <h5 className="agent-preview__heading">工具與風險</h5>
            {selectedTools.length === 0 ? (
              <p className="muted">此 Agent 沒有任何工具權限。</p>
            ) : (
              <ul className="agent-preview__list">
                {selectedTools.map((tool) => (
                  <li key={tool.name}>
                    <span>
                      {tool.name} · {tool.description}
                    </span>
                    <span
                      className={`badge badge--${
                        tool.risk === 'write' || tool.risk === 'privileged' ? 'admin' : 'user'
                      }`}
                    >
                      風險：{tool.risk}
                    </span>
                  </li>
                ))}
              </ul>
            )}

            <h5 className="agent-preview__heading">Skill revision pins</h5>
            {preview.length === 0 ? (
              <p className="muted">此 Agent 沒有任何 Skill 綁定。</p>
            ) : (
              <ul className="agent-preview__list">
                {preview.map((p) => (
                  <li key={p.skill}>
                    <span>{p.skill}</span>
                    {p.missing ? (
                      <span className="field-error">失效,無法固定</span>
                    ) : (
                      <span className="badge badge--user">
                        固定到 {p.pinned_revision != null ? `r${p.pinned_revision}` : '目前 revision'}
                      </span>
                    )}
                  </li>
                ))}
              </ul>
            )}
            {hasInvalidBinding && (
              <p className="field-error" role="alert">
                有失效的 Skill 綁定,請返回移除後再發布。
              </p>
            )}

            <h5 className="agent-preview__heading">Runtime / Execution Harness</h5>
            <dl className="agent-preview__summary">
              <div>
                <dt>工具／Context 輪數</dt>
                <dd>
                  {form.runtime_limits.max_tool_rounds} / {form.runtime_limits.max_context_rounds}
                </dd>
              </div>
              <div>
                <dt>Timeout</dt>
                <dd>{form.runtime_limits.timeout_seconds} 秒</dd>
              </div>
              <div>
                <dt>Token / step budget</dt>
                <dd>
                  {form.runtime_limits.token_budget} / {form.runtime_limits.step_budget}
                </dd>
              </div>
              <div>
                <dt>Workflow pin</dt>
                <dd>
                  {form.runtime_workflow
                    ? `${form.runtime_workflow.id} · r${form.runtime_workflow.revision}`
                    : 'Default Agent-Runtime Workflow（由 server 固定）'}
                </dd>
              </div>
            </dl>

            <div className="confirm-dialog__actions">
              <button
                ref={previewCancelRef}
                className="btn"
                type="button"
                onClick={closePreview}
                disabled={busy}
              >
                取消
              </button>
              <button
                ref={previewConfirmRef}
                className="btn btn--primary"
                type="button"
                onClick={() => void onPublish()}
                disabled={busy || !canPublish}
              >
                {busy ? '發布中…' : '確認發布'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
