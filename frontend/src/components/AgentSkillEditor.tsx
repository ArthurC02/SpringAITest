import { useEffect, useRef, useState } from 'react'
import { ApiError } from '../api/http'
import { exportSkill, getSkillPackage, importSkill } from '../api/skills'
import {
  extractDescription,
  extractName,
  readPackage,
  READONLY_DIR,
  setDescription,
  writePackage,
  type PackageEntry,
} from '../skills/agenticPackage'
import ErrorText from './ErrorText'
import Skeleton from './Skeleton'
import { useToast } from './Toast'

interface Props {
  name: string
  /** 匯入成功後回呼（重載清單）。 */
  onSaved: () => void
  onClose: () => void
}

/** references/ 或 assets/（scripts/ 於 P1 唯讀，不可新增）。 */
type TargetDir = 'references/' | 'assets/'

function fmtBytes(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  return `${(n / 1024 / 1024).toFixed(1)} MB`
}

/**
 * Agent Skill（agentic）package 編輯器：改 frontmatter（description + 其餘設定）、Markdown body
 * （runner instruction）與允許附件（references/、assets/；scripts/ 唯讀，P1 不執行）。
 * 存檔＝client 重打 zip → **import**（絕不走 flow-only createSkill/updateSkill，R3/AST-P2-004）。
 * server ApiError（422 等）時保留草稿並顯示訊息（AST-P2-003）。下載走 apiFetchBlob（Bearer/401 一致，AST-P2-005）。
 */
export default function AgentSkillEditor({ name, onSaved, onClose }: Props) {
  const toast = useToast()
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [frontmatter, setFrontmatter] = useState('')
  const [body, setBody] = useState('')
  const [resources, setResources] = useState<PackageEntry[]>([])
  const [target, setTarget] = useState<TargetDir>('references/')
  const [busy, setBusy] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const fileRef = useRef<HTMLInputElement>(null)
  // §4：SKILL.md/references/assets/scripts 以外的任意檔（根 LICENSE.txt、docs/… 等）編輯器不動，原樣帶回。
  const passthroughRef = useRef<PackageEntry[]>([])

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setLoadError(null)
    ;(async () => {
      try {
        const pkg = await readPackage(await getSkillPackage(name))
        if (cancelled) return
        setFrontmatter(pkg.frontmatter)
        setBody(pkg.body)
        setResources(pkg.resources)
        passthroughRef.current = pkg.passthrough
      } catch (e) {
        if (!cancelled) setLoadError((e as Error).message)
      } finally {
        if (!cancelled) setLoading(false)
      }
    })()
    return () => {
      cancelled = true
    }
  }, [name])

  const description = extractDescription(frontmatter)

  async function onAddFiles(list: FileList | null) {
    if (!list || list.length === 0) return
    const added: PackageEntry[] = []
    for (const f of Array.from(list)) {
      added.push({ path: target + f.name, bytes: new Uint8Array(await f.arrayBuffer()) })
    }
    setResources((rs) => {
      const map = new Map(rs.map((r) => [r.path, r]))
      for (const a of added) map.set(a.path, a)
      return [...map.values()].sort((a, b) => a.path.localeCompare(b.path))
    })
    if (fileRef.current) fileRef.current.value = ''
  }

  function onRemove(path: string) {
    setResources((rs) => rs.filter((r) => r.path !== path))
  }

  async function onSave() {
    if (extractName(frontmatter) !== name) {
      const msg = `已存在的 Agent Skill 名稱不可變更，請保持名稱為「${name}」；如需建立不同名稱的技能，請改用匯入套件建立新技能。`
      setSaveError(msg)
      toast(msg, 'error')
      return
    }
    setBusy(true)
    setSaveError(null)
    try {
      const bytes = writePackage({ frontmatter, body, resources, passthrough: passthroughRef.current })
      // ponytail: Blob 包 Uint8Array 給 FormData；import 送原始 zip bytes，接受與否由 server 端驗證。
      const zip = new Blob([bytes as unknown as BlobPart], { type: 'application/zip' })
      const stored = await importSkill(zip, `${name}.zip`)
      toast(`已匯入 ${stored.name}（r${stored.current_revision}）`, 'success')
      onSaved()
    } catch (e) {
      // 保留草稿（不清 state），顯示 ApiError 訊息（AST-P2-003）。
      const msg = e instanceof ApiError ? e.message : (e as Error).message
      setSaveError(msg)
      toast(msg, 'error')
    } finally {
      setBusy(false)
    }
  }

  async function onDownload() {
    setBusy(true)
    setSaveError(null)
    try {
      await exportSkill(name)
    } catch (e) {
      const msg = (e as Error).message
      setSaveError(msg)
      toast(msg, 'error')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="skill-editor">
      <div className="skill-editor__head">
        <h3 className="skill-editor__title">
          編輯 {name} <span className="badge badge--user">Agent Skill</span>
        </h3>
        <div className="skill-editor__actions">
          <button className="btn btn--primary" type="button" onClick={onSave} disabled={busy || loading}>
            {busy ? '儲存中…' : '儲存（重新匯入）'}
          </button>
          <button className="btn" type="button" onClick={onDownload} disabled={busy || loading}>
            下載套件
          </button>
          <button className="btn" type="button" onClick={onClose} disabled={busy}>
            關閉
          </button>
        </div>
      </div>

      <ErrorText msg={loadError} />
      <ErrorText msg={saveError} />

      {loading ? (
        <Skeleton rows={4} />
      ) : loadError ? null : (
        <div className="agent-pkg">
          <div className="field">
            <label htmlFor="agent-desc">描述</label>
            <input
              id="agent-desc"
              className="input"
              value={description}
              placeholder="這個 Agent Skill 做什麼"
              onChange={(e) => setFrontmatter((fm) => setDescription(fm, e.target.value))}
            />
          </div>

          <div className="field">
            <label htmlFor="agent-fm">設定（SKILL.md frontmatter）</label>
            <textarea
              id="agent-fm"
              className="textarea"
              rows={8}
              spellCheck={false}
              value={frontmatter}
              onChange={(e) => setFrontmatter(e.target.value)}
            />
            <p className="muted" role="note">
              已存在的 Agent Skill 名稱不可變更，必須維持為 <strong>{name}</strong>。如需使用不同名稱，請改用匯入套件建立新技能。
            </p>
            <p className="muted">
              標準欄位在頂層（name、description、allowed-tools、license/compatibility），引擎專屬欄位收在
              metadata（kind: agentic、required_role、timeout_seconds、input_schema 為 JSON 字串），由引擎驗證；描述可用上方欄位快速修改。
            </p>
          </div>

          <div className="field">
            <label htmlFor="agent-body">指令（Markdown body）</label>
            <textarea
              id="agent-body"
              className="textarea"
              rows={12}
              spellCheck={false}
              value={body}
              onChange={(e) => setBody(e.target.value)}
            />
          </div>

          <div className="field">
            <label>附件</label>
            <div className="agent-pkg__add">
              <select
                className="input"
                aria-label="附件目錄"
                value={target}
                onChange={(e) => setTarget(e.target.value as TargetDir)}
              >
                <option value="references/">references/</option>
                <option value="assets/">assets/</option>
              </select>
              <input
                ref={fileRef}
                type="file"
                multiple
                aria-label="新增附件"
                onChange={(e) => onAddFiles(e.target.files)}
              />
            </div>
            {resources.length === 0 ? (
              <p className="muted">尚無附件。</p>
            ) : (
              <ul className="agent-pkg__files">
                {resources.map((r) => {
                  const readOnly = r.path.startsWith(READONLY_DIR)
                  return (
                    <li key={r.path}>
                      <span className="agent-pkg__path">{r.path}</span>
                      <span className="muted">{fmtBytes(r.bytes.length)}</span>
                      {readOnly ? (
                        <span className="badge badge--user">唯讀（附件不會被執行）</span>
                      ) : (
                        <button
                          className="btn btn--danger"
                          type="button"
                          onClick={() => onRemove(r.path)}
                          disabled={busy}
                        >
                          移除
                        </button>
                      )}
                    </li>
                  )
                })}
              </ul>
            )}
          </div>
        </div>
      )}
    </section>
  )
}
