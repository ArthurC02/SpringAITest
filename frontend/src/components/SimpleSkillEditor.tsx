import { Suspense, lazy, useState } from 'react'
import { ApiError } from '../api/http'
import { createSkill, listSkillCatalog, validateSkill } from '../api/skills'
import type { SkillInputField, SkillValidation } from '../types'
import { compose } from '../skills/compose'
import { TEMPLATES, type SkillForm, type SkillTemplate } from '../skills/templates'
import { CODE_LABEL, blockingErrors } from '../skills/validationLabels'
import ErrorText from './ErrorText'
import Skeleton from './Skeleton'
import SkillRunPanel from './SkillRunPanel'

// CodeMirror 只在切到 Python 時才動態載入（不進首屏 bundle，SSR-P2C-001）。
const PythonEditor = lazy(() => import('./PythonEditor'))

interface Props {
  // ponytail: 只服務「新增」——既有 custom 的編輯改走 AdvancedSkillEditor 保真（SkillHome），
  // 簡單模式無法反解 YAML，留 edit 分支只會靜默重建、丟原定義（設計 §10 偏差②）。
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
const EXTRA_FIELDS: (keyof SkillForm)[] = ['topK', 'sortBy', 'metric', 'period']

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
export default function SimpleSkillEditor({ onSaved, onAdvanced, onClose }: Props) {
  const [templateId, setTemplateId] = useState<SkillTemplate['id'] | null>(null)
  const [form, setForm] = useState<SkillForm>({})
  const [ruleLang, setRuleLang] = useState<'nl' | 'python'>('nl')
  const [baseDefinition, setBaseDefinition] = useState<string | null>(null)
  const [storedSchema, setStoredSchema] = useState<Record<string, SkillInputField> | null>(null)
  const [validation, setValidation] = useState<SkillValidation | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [savedName, setSavedName] = useState<string | null>(null) // 存後才啟用試一下

  const template = templateId ? TEMPLATES.find((t) => t.id === templateId)! : null

  // 選範本 → 從 catalog 取骨架原文（唯一事實來源，取不到就擋存，不 fallback 前端 skeleton）。
  async function pickTemplate(t: SkillTemplate) {
    setTemplateId(t.id)
    setRuleLang(t.slotKind === 'script' ? 'python' : 'nl')
    setBaseDefinition(null)
    setError(null)
    setValidation(null)
    try {
      const catalog = await listSkillCatalog()
      const entry = catalog.find((e) => e.name === t.basedOn)
      if (!entry?.definition) {
        setError('找不到這個範本的骨架設定，暫時無法用它建立。請稍後再試或改用進階編輯。')
        return
      }
      setBaseDefinition(entry.definition)
      setStoredSchema(entry.input_schema ?? null)
    } catch (e) {
      setError((e as Error).message)
    }
  }

  function setField(field: keyof SkillForm, value: string) {
    setForm((f) => ({ ...f, [field]: value }))
  }

  function composed(): string | null {
    if (!template || !baseDefinition) return null
    return compose(template, form, baseDefinition)
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
    // 名稱空白時 compose 不覆寫 __SLOT_name__ → 會用範本預設名（template_*）靜默建骨架，故先擋。
    // ponytail: 前端 guard 是主要修法；後端 ReservedNames 加 template_ 前綴兜底是升級路徑（本次不做）。
    const name = form.name?.trim()
    if (!name) {
      setError('請先為這個 Skill 取一個名稱。')
      return
    }
    setBusy(true)
    setError(null)
    setValidation(null)
    try {
      const def = compose(template, form, baseDefinition)
      const v = await validateSkill(def)
      if (!v.valid && blockingErrors(v).length > 0) {
        setValidation(v)
        return
      }
      await createSkill(def)
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

  return (
    <div className="simple-skill">
      <div className="skill-editor__head">
        <h3 className="skill-editor__title">新增 Skill</h3>
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

      {/* ① 從範本開始 */}
      <section className="simple-skill__block">
        <h4 className="simple-skill__block-title">① 從範本開始</h4>
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
      </section>

      {/* ② 名稱 */}
      <section className="simple-skill__block">
        <h4 className="simple-skill__block-title">② 名稱</h4>
        <input
          className="input"
          value={form.name ?? ''}
          placeholder="例如：sales-rule"
          onChange={(e) => setField('name', e.target.value)}
        />
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
          <label className="simple-skill__toggle">
            <input
              type="checkbox"
              checked={ruleLang === 'python'}
              onChange={(e) => setRuleLang(e.target.checked ? 'python' : 'nl')}
            />
            進階：改用 Python 編輯規則
          </label>
          {ruleLang === 'python' ? (
            <Suspense fallback={<Skeleton rows={3} />}>
              <PythonEditor value={form.rule ?? ''} onChange={(v) => setField('rule', v)} />
            </Suspense>
          ) : (
            <textarea
              className="textarea"
              value={form.rule ?? ''}
              placeholder="用中文寫就好，可留空"
              onChange={(e) => setField('rule', e.target.value)}
            />
          )}
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
          disabled={busy || !template || !baseDefinition || !form.name?.trim()}
        >
          {busy ? '儲存中…' : '儲存'}
        </button>
      </div>

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
