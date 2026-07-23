// Agent Skill (agentic) package 的 client 端讀寫：解析 SKILL.md frontmatter/body、列出附件、
// 重新打包 zip → import。**client 端解析只供預覽/編輯**，接受與否一律以 server import validation 為準。
// zip 編解碼用 fflate（唯一新增相依，已釘 0.8.2）——不手刻 zip encoder（壞 zip 只會 server 端驗證失敗、白跑一趟）。
import { strFromU8, strToU8, unzipSync, zipSync } from 'fflate'

/** package 內一個附件（references/、assets/、scripts/ 下的檔案；不含根目錄 SKILL.md）。 */
export interface PackageEntry {
  path: string
  bytes: Uint8Array
}

/** 解析後的 agentic package（供編輯器呈現/重打包）。 */
export interface AgentPackage {
  /** SKILL.md 的 YAML frontmatter 原文（--- 圍籬之間，含 description）。 */
  frontmatter: string
  /** SKILL.md body（第二個 --- 之後的 Markdown 指令，即 runner instruction）。 */
  body: string
  /** references/、assets/、scripts/ 下的附件（編輯器 UI 特別呈現）。 */
  resources: PackageEntry[]
  /** 其餘所有非 SKILL.md 檔案（根 LICENSE.txt、docs/… 等任意檔）。編輯器不動，原樣帶回（§4 標準允許任意額外檔）。 */
  passthrough: PackageEntry[]
}

const ALLOWED_DIRS = ['references/', 'assets/', 'scripts/'] as const

/** scripts/ 於 P1 不執行 → 編輯器唯讀顯示、停用刪除/新增（R8 / D7）。 */
export const READONLY_DIR = 'scripts/'

function isResourcePath(path: string): boolean {
  return !path.endsWith('/') && ALLOWED_DIRS.some((d) => path.startsWith(d))
}

/** 拆 SKILL.md 為 frontmatter + body（無 frontmatter 圍籬時保守地全視為 body，讓作者仍可補）。 */
function splitSkillMd(md: string): { frontmatter: string; body: string } {
  const text = md.replace(/^﻿/, '')
  const m = text.match(/^---\r?\n([\s\S]*?)\r?\n---\r?\n?([\s\S]*)$/)
  if (!m) return { frontmatter: '', body: text }
  return { frontmatter: m[1], body: m[2] }
}

/** frontmatter + body → SKILL.md 原文（固定用 LF、標準 --- 圍籬）。 */
export function assembleSkillMd(frontmatter: string, body: string): string {
  return `---\n${frontmatter.replace(/\s+$/, '')}\n---\n\n${body.replace(/^\s+/, '')}`
}

/** 讀 export 回來的 zip → AgentPackage。找不到 SKILL.md 視為非 agentic package（拋錯）。 */
export async function readPackage(blob: Blob): Promise<AgentPackage> {
  const buf = new Uint8Array(await blob.arrayBuffer())
  const files = unzipSync(buf)
  const skillMd = files['SKILL.md']
  if (!skillMd) {
    throw new Error('這個 skill 沒有 SKILL.md，無法以 Agent Skill 編輯器開啟。')
  }
  const { frontmatter, body } = splitSkillMd(strFromU8(skillMd))
  const resources: PackageEntry[] = []
  const passthrough: PackageEntry[] = []
  for (const [path, bytes] of Object.entries(files)) {
    if (path === 'SKILL.md' || path.endsWith('/')) continue
    ;(isResourcePath(path) ? resources : passthrough).push({ path, bytes })
  }
  const byPath = (a: PackageEntry, b: PackageEntry) => a.path.localeCompare(b.path)
  resources.sort(byPath)
  passthrough.sort(byPath)
  return { frontmatter, body, resources, passthrough }
}

/** AgentPackage → zip bytes（SKILL.md + 全部附件 + passthrough）。重打包後走 import，由 server 驗證接受與否。 */
export function writePackage(pkg: AgentPackage): Uint8Array {
  const files: Record<string, Uint8Array> = {
    'SKILL.md': strToU8(assembleSkillMd(pkg.frontmatter, pkg.body)),
  }
  for (const r of pkg.passthrough) files[r.path] = r.bytes
  for (const r of pkg.resources) files[r.path] = r.bytes
  return zipSync(files)
}

/** frontmatter 取出 description 值（單行；供專屬欄位顯示）。取不到回空字串。 */
export function extractDescription(frontmatter: string): string {
  const m = frontmatter.match(/^description:[ \t]*(.*)$/m)
  if (!m) return ''
  const raw = m[1].trim()
  // 雙引號純量須以 JSON.parse 正確反轉義（inverse of setDescription 的 JSON.stringify）。
  if (raw.startsWith('"')) {
    try {
      return JSON.parse(raw) as string
    } catch {
      return raw.replace(/^"|"$/g, '')
    }
  }
  return raw.replace(/^'|'$/g, '')
}

/** 把 description 值寫回 frontmatter 的 description 行（無則補在最前）。用 JSON.stringify 產生 YAML-safe 雙引號純量。 */
export function setDescription(frontmatter: string, value: string): string {
  // ponytail: 只處理單行 description（非技術作者常態）；folded/多行 description 屬邊角，改由 frontmatter 直接編輯。
  const line = `description: ${JSON.stringify(value)}`
  if (/^description:.*$/m.test(frontmatter)) {
    return frontmatter.replace(/^description:.*$/m, line)
  }
  return frontmatter ? `${line}\n${frontmatter}` : line
}

/**
 * 由儲存的 definition 判斷是否 agentic。標準對齊後 canonical 投影把 `kind` 收進 `metadata`
 * （§3：頂層只有標準欄位），故 `kind: agentic` 現在縮排在 `metadata:` 之下——比對時允許前導空白。
 * catalog 未帶獨立 `kind` 欄，故這是可靠訊號；缺/壞 → 視為 flow（fall back gracefully）。
 */
export function isAgenticDefinition(definition?: string | null): boolean {
  return !!definition && /^[ \t]*kind:[ \t]*agentic[ \t]*$/m.test(definition)
}
