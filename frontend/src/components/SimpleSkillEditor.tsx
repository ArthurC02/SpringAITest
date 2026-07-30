import { useEffect, useRef, useState } from 'react'
import { ApiError } from '../api/http'
import { listSkillCatalog } from '../api/skills'
import {
  createBusinessWorkflow,
  updateBusinessWorkflow,
  validateBusinessWorkflow,
} from '../api/businessWorkflows'
import type { SkillInputField, SkillValidation } from '../types'
import { compose } from '../skills/compose'
import { TEMPLATES, type SkillForm, type SkillTemplate } from '../skills/templates'
import { CODE_LABEL, WARN_CODES, blockingErrors } from '../skills/validationLabels'
import { NAME_RULE_MESSAGE, isValidSkillName, slugifySkillName } from '../skills/skillName'
import ErrorText from './ErrorText'
import SkillRunPanel from './SkillRunPanel'

interface Props {
  // create 模式（無 initial）：從範本起手。
  // edit 模式（有 initial）：讀回已存的 simpleForm 重填——只有簡單模式建立品有這份表單狀態，
  // 故不會靜默重建、丟原定義；範本與名稱鎖定，存檔依表單重跑 compose 覆蓋 definition。
  initial?: { name: string; templateId: string; form: SkillForm }
  onSaved: (name: string) => void
  onAdvanced: (definition: string) => void
  onClose: () => void
}

/** 簡單模式存檔失敗翻人話（非技術使用者：不透傳引擎/YAML 原始訊息，比照 §SSR-P2B-007）。 */
function saveErrorMessage(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.status === 409) return '這個名稱已被使用，請換一個。'
    if (e.status === 422) return '設定有誤，請調整規則或欄位後再試。'
  }
  return '儲存失敗，請稍後再試。'
}

// 額外開放欄位（name/description/rule 有專屬區塊，這裡只放工作流程參數）。
const EXTRA_FIELDS: (keyof SkillForm)[] = ['topK']

/** 驗證錯誤翻人話：只給 CODE_LABEL 人話，避開行號與 node/state 術語（SSR-P2B-007）。 */
function humanErrors(v: SkillValidation): string[] {
  const blocking = blockingErrors(v)
  const labels = blocking.map((e) => CODE_LABEL[e.code] ?? '設定有誤，請調整規則或欄位後再試。')
  return [...new Set(labels)]
}

/**
 * 簡單模式（雙門其一）：四塊零術語外殼（範本/名稱/描述/我的規則）＋ 存後試 ＋ 版本。
 * 不揭露流程/node/state/YAML。未選範本不可存、永不從空白 YAML 起手（SSR-P2B-001）。
 */
export default function SimpleSkillEditor({ initial, onSaved, onAdvanced, onClose }: Props) {
  const editing = !!initial
  // 編輯模式：以 basedOn（存下的 templateId）反查範本；查不到 → 引導改用進階（見掛載 effect）。
  const initialTemplate = initial ? TEMPLATES.find((t) => t.basedOn === initial.templateId) ?? null : null

  const [templateId, setTemplateId] = useState<SkillTemplate['id'] | null>(initialTemplate?.id ?? null)
  const [form, setForm] = useState<SkillForm>(initial?.form ?? {})
  const [baseDefinition, setBaseDefinition] = useState<string | null>(null)
  const [storedSchema, setStoredSchema] = useState<Record<string, SkillInputField> | null>(null)
  const [validation, setValidation] = useState<SkillValidation | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [savedName, setSavedName] = useState<string | null>(null) // 存後才啟用試一下
  const skeletonGenerationRef = useRef(0)

  const template = templateId ? TEMPLATES.find((t) => t.id === templateId)! : null

  // 從 catalog 取骨架原文（唯一事實來源，取不到就擋存，不 fallback 前端 skeleton）。不動 form/templateId。
  async function loadSkeleton(t: SkillTemplate) {
    const generation = ++skeletonGenerationRef.current
    setBaseDefinition(null)
    setError(null)
    setValidation(null)
    try {
      const catalog = await listSkillCatalog()
      if (generation !== skeletonGenerationRef.current) return
      const entry = catalog.find((e) => e.name === t.basedOn)
      if (!entry?.definition) {
        setError('找不到這個範本的骨架設定，暫時無法用它建立。請稍後再試或改用進階編輯。')
        return
      }
      setBaseDefinition(entry.definition)
      setStoredSchema(entry.input_schema ?? null)
    } catch (e) {
      if (generation === skeletonGenerationRef.current) setError((e as Error).message)
    }
  }

  function pickTemplate(t: SkillTemplate) {
    setTemplateId(t.id)
    void loadSkeleton(t)
  }

  // 編輯模式掛載：範本查得到就自動載骨架；查不到 → 顯示一句錯誤並引導改用進階編輯，不崩。
  useEffect(() => {
    if (initial) {
      if (!initialTemplate) {
        setError('找不到這個 Skill 使用的範本，無法用簡單模式編輯。請改用清單的「編輯」（進階編輯器）。')
      } else {
        void loadSkeleton(initialTemplate)
      }
    }
    return () => {
      skeletonGenerationRef.current += 1
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- 只在掛載跑一次；initial 由呼叫端固定。
  }, [])

  function setField(field: keyof SkillForm, value: string) {
    setForm((f) => ({ ...f, [field]: value }))
  }

  function composed(): string | null {
    if (!template || !baseDefinition) return null
    return compose(form, baseDefinition)
  }

  async function onSave() {
    if (!template) {
      setError('請先從上方選一個範本。')
      return
    }
    if (!baseDefinition) {
      setError('這個範本的骨架設定尚未取得，無法儲存。')
      return
    }
    // 名稱空白時 compose 不覆寫 __SLOT_name__ → 會用範本預設名（template-*）靜默建骨架，故先擋。
    // ponytail: 前端 guard 是主要修法；後端 ReservedNames 加 template- 前綴兜底是升級路徑（本次不做）。
    const name = form.name?.trim()
    if (!name) {
      setError('請先為這個 Skill 取一個名稱。')
      return
    }
    // 名稱是技術識別碼(slug):不合規就在前端擋下,不送出 —— 內聯 field-error 已提示規則與建議,
    // 避免非技術使用者看到 server 把中文名稱誤標成 flow 的錯誤。
    if (!isValidSkillName(name)) return
    setBusy(true)
    setError(null)
    setValidation(null)
    try {
      const def = compose(form, baseDefinition)
      const v = await validateBusinessWorkflow(def)
      if (!v.valid && blockingErrors(v).length > 0) {
        setValidation(v)
        return
      }
      // 存下範本身分＋表單原值，讓此 skill 日後可重回簡單模式（templateId 用 basedOn）。
      const simpleForm = { templateId: template.basedOn, form }
      if (editing) {
        await updateBusinessWorkflow(name, def, simpleForm)
      } else {
        await createBusinessWorkflow(def, simpleForm)
      }
      // 存檔成功。保留 validation 以便顯示非阻擋的資料流警告（不阻擋存檔，只提醒試跑）。
      setValidation(v)
      setSavedName(name)
      onSaved(name)
    } catch (e) {
      // 後端仍可能回 409/422：依 status 翻人話，不透傳原始訊息（低1）。
      setError(saveErrorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  function onAdvancedClick() {
    const def = composed()
    if (def) onAdvanced(def)
  }

  const errorLabels = validation ? humanErrors(validation) : []
  const showField = (f: keyof SkillForm) => template?.openFields.includes(f)

  // 存檔成功後的非阻擋警告：只彙總成一句人話（來自 validationLabels 的警告碼），不逐條、
  // 不顯示引擎原文（含 node/state 術語）—— 逐條渲染是進階編輯器的事。
  const warnCount = validation ? validation.errors.filter((e) => WARN_CODES.has(e.code)).length : 0

  // 名稱即時 slug 驗證(存檔前擋);不合規時給一個可一鍵套用的建議 slug。
  const nameTrimmed = form.name?.trim() ?? ''
  const nameError = nameTrimmed !== '' && !isValidSkillName(nameTrimmed) ? NAME_RULE_MESSAGE : null
  const nameSuggestion = nameError ? slugifySkillName(nameTrimmed) : ''

  return (
    <div className="simple-skill">
      <div className="skill-editor__head">
        <h3 className="skill-editor__title">{editing ? '簡單模式編輯' : '新增業務流程'}</h3>
        <div className="skill-editor__actions">
          <button
            className="btn"
            type="button"
            onClick={onAdvancedClick}
            disabled={busy || !composed()}
          >
            進階編輯
          </button>
          <button className="btn" type="button" onClick={onClose} disabled={busy}>
            關閉
          </button>
        </div>
      </div>

      {/* 編輯模式常駐警告：簡單模式存檔會依表單重建定義，覆蓋任何進階編輯器的手改。 */}
      {editing && (
        <p className="simple-skill__warn" role="note">
          以簡單模式儲存會依表單重建定義；若曾在進階編輯器手動修改，這些修改將被覆蓋。
        </p>
      )}

      {/* ① 從範本開始 */}
      <section className="simple-skill__block">
        <h4 className="simple-skill__block-title">① 從範本開始</h4>
        {editing ? (
          <p className="muted">
            範本：{template ? template.label : initial!.templateId}（編輯模式不可更換）
          </p>
        ) : (
          <div className="simple-skill__templates" role="radiogroup" aria-label="範本">
            {TEMPLATES.map((t) => (
              <label
                key={t.id}
                className={`simple-skill__template${templateId === t.id ? ' simple-skill__template--on' : ''}`}
              >
                <input
                  type="radio"
                  name="template"
                  checked={templateId === t.id}
                  onChange={() => pickTemplate(t)}
                />
                {t.label}
              </label>
            ))}
          </div>
        )}
      </section>

      {/* ② 名稱 */}
      <section className="simple-skill__block">
        <h4 className="simple-skill__block-title">② 名稱</h4>
        <div className="field">
          <input
            className="input"
            value={form.name ?? ''}
            placeholder="例如：sales-rule"
            aria-invalid={!!nameError}
            // 名稱是 skill 識別，改名等於另建 → 編輯模式鎖定。
            disabled={editing}
            onChange={(e) => setField('name', e.target.value)}
          />
          {editing && <p className="muted">名稱是 Skill 的識別碼，無法在編輯模式變更。</p>}
          {nameError && (
            <span className="field-error" role="alert">
              {nameError}
              {nameSuggestion && (
                <>
                  {' '}建議使用{' '}
                  <button
                    type="button"
                    className="btn"
                    onClick={() => setField('name', nameSuggestion)}
                  >
                    {nameSuggestion}
                  </button>
                </>
              )}
            </span>
          )}
        </div>
      </section>

      {/* ③ 描述 */}
      <section className="simple-skill__block">
        <h4 className="simple-skill__block-title">③ 描述</h4>
        <input
          className="input"
          value={form.description ?? ''}
          placeholder="這個 Skill 做什麼"
          onChange={(e) => setField('description', e.target.value)}
        />
      </section>

      {/* ④ 工作流程（唯讀顯示所選範本 + 開放的流程參數） */}
      {template && (
        <section className="simple-skill__block">
          <h4 className="simple-skill__block-title">④ 工作流程</h4>
          <p className="muted">使用範本「{template.label}」的既定流程，你只需填下面的欄位。</p>
          {EXTRA_FIELDS.filter(showField).map((f) => (
            <div className="field" key={f}>
              <label htmlFor={`sf-${f}`}>{template.labels[f] ?? f}</label>
              <input
                id={`sf-${f}`}
                className="input"
                type={template.inputWidgets[f] === 'number' ? 'number' : 'text'}
                value={form[f] ?? ''}
                onChange={(e) => setField(f, e.target.value)}
              />
            </div>
          ))}
        </section>
      )}

      {/* ⑤ 我的規則 */}
      {template && showField('rule') && (
        <section className="simple-skill__block">
          <h4 className="simple-skill__block-title">⑤ {template.labels.rule ?? '我的規則'}</h4>
          <textarea
            className="textarea"
            value={form.rule ?? ''}
            placeholder="用中文寫就好，可留空"
            onChange={(e) => setField('rule', e.target.value)}
          />
        </section>
      )}

      <ErrorText msg={error} />
      {errorLabels.length > 0 && (
        <div className="simple-skill__errors" role="alert">
          <p>還不能儲存，請調整後再試：</p>
          <ul>
            {errorLabels.map((l) => (
              <li key={l}>{l}</li>
            ))}
          </ul>
        </div>
      )}

      <div className="simple-skill__actions">
        <button
          className="btn btn--primary"
          type="button"
          onClick={onSave}
          disabled={busy || !template || !baseDefinition || !form.name?.trim() || !!nameError}
        >
          {busy ? '儲存中…' : '儲存'}
        </button>
      </div>

      {/* 存檔成功後的非阻擋資料流警告：一句彙總、role="status"（非 alert，不阻擋）。 */}
      {savedName && warnCount > 0 && (
        <p className="simple-skill__warn" role="status">
          已儲存。有 {warnCount} 個欄位可能取不到資料，建議先在下方試跑確認。
        </p>
      )}

      {/* ⑥ 試一下（存後試：未存停用，存後走正式 invoke） */}
      <section className="simple-skill__block">
        <h4 className="simple-skill__block-title">⑥ 試一下</h4>
        {savedName ? (
          <SkillRunPanel name={savedName} inputSchema={storedSchema} />
        ) : (
          <p className="muted">先儲存後才能試跑（試跑會執行已存在的 Skill）。</p>
        )}
      </section>
    </div>
  )
}
