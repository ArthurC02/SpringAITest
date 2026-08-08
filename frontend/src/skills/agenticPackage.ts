// Agent Skill (agentic) package 的 client 端讀寫：解析 SKILL.md frontmatter/body、列出附件、
// 重新打包 zip → import。**client 端解析只供預覽/編輯**，接受與否一律以 server import validation 為準。
// zip 編解碼用 fflate（唯一新增相依，已釘 0.8.2）——不手刻 zip encoder（壞 zip 只會 server 端驗證失敗、白跑一趟）。
import { strFromU8, strToU8, unzipSync, zipSync } from 'fflate'
// ponytail: 這裡刻意帶 `.ts` 副檔名（tsconfig allowImportingTsExtensions），讓
// scripts/agenticPackage.selfcheck.ts 能用 Node 原生 strip types 直接跑；bundler 解析不受影響。
import { isValidSkillName } from './skillName.ts'

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
function assembleSkillMd(frontmatter: string, body: string): string {
  return `---\n${frontmatter.replace(/\s+$/, '')}\n---\n\n${body.replace(/^\s+/, '')}`
}

/**
 * 標準佈局 `{name}/SKILL.md`（標準 §0：資料夾名＝name）→ 剝除單一頂層資料夾前綴。語義對齊 workflow
 * `app/engine/package.py` 的 `_strip_single_root_folder`：只在 root 沒有 SKILL.md、且**所有** entry
 * 共用同一個頂層資料夾時才剝除，否則原樣回傳（舊的 root-SKILL.md 佈局不受影響）。
 */
function stripSingleRootFolder(files: Record<string, Uint8Array>): Record<string, Uint8Array> {
  const paths = Object.keys(files)
  const roots = new Set(paths.map((p) => p.split('/', 1)[0]))
  if (roots.size !== 1) return files
  const prefix = `${[...roots][0]}/`
  // 有與資料夾同名的 root 檔案 → 不是單一頂層資料夾結構。
  if (!paths.every((p) => p.startsWith(prefix))) return files
  return Object.fromEntries(paths.map((p) => [p.slice(prefix.length), files[p]]))
}

/**
 * frontmatter 的 name（＝ skill 名稱 ＝ 標準要求的頂層資料夾名 ＝ server 端 folder_name_mismatch
 * 的比對對象）。取不到或不合 skill 名稱規則 → 空字串（呼叫端退回 root 佈局，讓 server 報更精準的
 * frontmatter 錯誤）。合規名稱不含 `/`、`.`、`\`、drive 字元 → 拿來當前綴沒有路徑注入面。
 */
export function extractName(frontmatter: string): string {
  // 取最後一個 name:：重複鍵時 PyYAML 取最後一個，前綴必須跟 server 認定的 name 同一個，
  // 否則病態的重複 frontmatter 會讓自家寫出的 zip 被 folder_name_mismatch 擋下。
  const m = [...frontmatter.matchAll(/^name:[ \t]*(.*)$/gm)].at(-1)
  // 合法 skill name 是純 slug，永遠不需要跳脫 → 去掉可能的引號即可，不必走 YAML 解析。
  const name = m ? m[1].trim().replace(/^["']|["']$/g, '') : ''
  return isValidSkillName(name) ? name : ''
}

/** 讀 export 回來的 zip → AgentPackage。找不到 SKILL.md 視為非 agentic package（拋錯）。 */
export async function readPackage(blob: Blob): Promise<AgentPackage> {
  const buf = new Uint8Array(await blob.arrayBuffer())
  // zip 的資料夾 entry 以 `/` 結尾、內容為空；先濾掉，前綴判定與後續處理都只看真正的檔案。
  let files = Object.fromEntries(
    Object.entries(unzipSync(buf)).filter(([path]) => !path.endsWith('/')),
  )
  if (!files['SKILL.md']) files = stripSingleRootFolder(files)
  const skillMd = files['SKILL.md']
  if (!skillMd) {
    throw new Error('這個 skill 沒有 SKILL.md，無法以 Agent Skill 編輯器開啟。')
  }
  const { frontmatter, body } = splitSkillMd(strFromU8(skillMd))
  const resources: PackageEntry[] = []
  const passthrough: PackageEntry[] = []
  for (const [path, bytes] of Object.entries(files)) {
    if (path === 'SKILL.md') continue
    ;(isResourcePath(path) ? resources : passthrough).push({ path, bytes })
  }
  const byPath = (a: PackageEntry, b: PackageEntry) => a.path.localeCompare(b.path)
  resources.sort(byPath)
  passthrough.sort(byPath)
  return { frontmatter, body, resources, passthrough }
}

/**
 * AgentPackage → zip bytes（SKILL.md + 全部附件 + passthrough）。重打包後走 import，由 server 驗證接受與否。
 * 一律輸出標準的 `{name}/…` 單一頂層資料夾佈局（標準 §0），name 取自 frontmatter —— import 走
 * `/api/skills/import`（無路徑 name，server 由 SKILL.md 推導名稱），所以前綴＝frontmatter name
 * ＝ server 認定的 skill 名稱，三者天生同步，改名也跟著改，不會觸發 folder_name_mismatch。
 * 讀進來是舊 root 佈局的 package 存檔後會升級成資料夾佈局（存檔本來就重寫 package bytes）。
 */
export function writePackage(pkg: AgentPackage): Uint8Array {
  const name = extractName(pkg.frontmatter)
  const prefix = name ? `${name}/` : ''
  const files: Record<string, Uint8Array> = {
    [`${prefix}SKILL.md`]: strToU8(assembleSkillMd(pkg.frontmatter, pkg.body)),
  }
  for (const r of pkg.passthrough) files[prefix + r.path] = r.bytes
  for (const r of pkg.resources) files[prefix + r.path] = r.bytes
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
