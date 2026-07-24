// Agent Skill package 讀寫的純函式自我檢查（標準 §0 單一頂層資料夾佈局）。
// 不打網路。Node 25 原生 strip types：`node scripts/agenticPackage.selfcheck.ts`。
import assert from 'node:assert/strict'
import { strToU8, unzipSync, zipSync } from 'fflate'
import { readPackage, writePackage } from '../src/skills/agenticPackage.ts'

const FM = 'name: my-skill\ndescription: "測試用"'
const SKILL_MD = strToU8(`---\n${FM}\n---\n\n做事情。\n`)

const zip = (files: Record<string, Uint8Array>) => new Blob([zipSync(files)])

// --- ① 標準佈局 {name}/SKILL.md 可開，附件剝除前綴後為相對路徑 ---
const std = await readPackage(
  zip({
    'my-skill/': new Uint8Array(0), // 資料夾 entry 不得影響前綴判定
    'my-skill/SKILL.md': SKILL_MD,
    'my-skill/scripts/x.py': strToU8('print(1)\n'),
    'my-skill/references/y.md': strToU8('# y\n'),
    'my-skill/LICENSE.txt': strToU8('MIT\n'),
  }),
)
assert.equal(std.frontmatter, FM, '① frontmatter 解析')
assert.deepEqual(
  std.resources.map((r) => r.path),
  ['references/y.md', 'scripts/x.py'],
  '① resources 剝除前綴',
)
assert.deepEqual(std.passthrough.map((r) => r.path), ['LICENSE.txt'], '① passthrough 剝除前綴')

// --- ② 舊的 root SKILL.md 佈局仍可開（相容回歸） ---
const legacy = await readPackage(
  zip({ 'SKILL.md': SKILL_MD, 'references/y.md': strToU8('# y\n') }),
)
assert.equal(legacy.frontmatter, FM, '② root 佈局 frontmatter')
assert.deepEqual(legacy.resources.map((r) => r.path), ['references/y.md'], '② root 佈局附件')

// --- ③ root 無 SKILL.md 且多個頂層 entry → 維持既有錯誤 ---
await assert.rejects(
  readPackage(zip({ 'a/SKILL.md': SKILL_MD, 'b/note.md': strToU8('x') })),
  /沒有 SKILL.md/,
  '③ 非單一頂層資料夾不剝除',
)
// 與資料夾同名的 root 檔案也不算單一頂層資料夾結構。
await assert.rejects(
  readPackage(zip({ 'a/SKILL.md': SKILL_MD, a: strToU8('x') })),
  /沒有 SKILL.md/,
  '③ 同名 root 檔案不剝除',
)

// --- ④ writePackage 一律輸出 {name}/ 佈局 ---
const written = unzipSync(writePackage(std))
assert.deepEqual(
  Object.keys(written).sort(),
  ['my-skill/LICENSE.txt', 'my-skill/SKILL.md', 'my-skill/references/y.md', 'my-skill/scripts/x.py'],
  '④ 全部 entry 在 {name}/ 底下',
)

// --- ⑤ round-trip 穩定 ---
const back = await readPackage(new Blob([writePackage(std)]))
assert.equal(back.frontmatter, std.frontmatter, '⑤ round-trip frontmatter')
assert.equal(back.body, std.body, '⑤ round-trip body')
assert.deepEqual(back.resources.map((r) => r.path), std.resources.map((r) => r.path), '⑤ round-trip 附件')
assert.deepEqual(
  Object.keys(unzipSync(writePackage(back))).sort(),
  Object.keys(written).sort(),
  '⑤ 二次寫出不再疊前綴',
)

// --- ⑥ frontmatter 無合法 name → 退回 root 佈局（交 server 報 frontmatter 錯誤） ---
const noName = { frontmatter: 'description: x', body: 'b', resources: [], passthrough: [] }
assert.deepEqual(Object.keys(unzipSync(writePackage(noName))), ['SKILL.md'], '⑥ 無 name → root 佈局')

console.log('agenticPackage.selfcheck: all assertions passed')
