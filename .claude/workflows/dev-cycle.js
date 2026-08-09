export const meta = {
  name: 'dev-cycle',
  description: 'Fixed dev pipeline: parallel implementers → bounded review/fix loop → simplify → optional e2e verify → docs',
  whenToUse:
    'Well-specified feature work with a written spec. args: {specPath|spec, areas:[platform|backend|frontend|workflow], crossService?:bool, docs?:bool}. Exploratory/ambiguous work should stay on per-phase delegation instead.',
  phases: [
    { title: 'Implement', detail: 'one implementer per stack, in parallel' },
    { title: 'Review', detail: 'code-reviewer; high/medium findings loop back to implementers (max 2 fix rounds)' },
    { title: 'Simplify', detail: 'behavior-preserving cleanup of the changed code' },
    { title: 'Verify', detail: 'e2e-verifier, only when crossService' },
    { title: 'Docs', detail: 'docs-updater sync, unless docs:false' },
  ],
}

// ---- args ----
const spec = args && args.specPath
  ? `規格檔路徑:${args.specPath} — 開工前先完整讀取該檔。`
  : args && args.spec
if (!spec) throw new Error('args 必須提供 specPath(規格檔路徑,建議)或 spec(規格全文)')

const AGENT_BY_AREA = {
  platform: 'dotnet-implementer',
  backend: 'dotnet-implementer',
  frontend: 'frontend-implementer',
  workflow: 'python-implementer',
}
const areas = Array.isArray(args && args.areas) ? args.areas : []
if (!areas.length) throw new Error('args.areas 必須是非空陣列,值為 platform|backend|frontend|workflow')
const invalid = areas.filter(a => !AGENT_BY_AREA[a])
if (invalid.length) throw new Error(`未知 area:${invalid.join(', ')}`)

// 同一 implementer 負責的 areas 合併成一次派工(platform+backend → 一個 dotnet-implementer)
const byAgent = {}
for (const a of areas) {
  const t = AGENT_BY_AREA[a]
  if (!byAgent[t]) byAgent[t] = []
  byAgent[t].push(a)
}
const implementers = Object.entries(byAgent)

// ---- Phase 1: Implement(並行)----
phase('Implement')
const implReports = (
  await parallel(
    implementers.map(([type, list]) => () =>
      agent(
        `${spec}\n\n你負責的範圍:${list.join('、')}。照規格逐字實作並把測試跑到全綠。完成後回報:變更檔案清單、測試統計(總數/通過/失敗)、任何偏離規格的決定與原因。`,
        { agentType: type, label: `impl:${list.join('+')}`, phase: 'Implement' }
      )
    )
  )
).filter(Boolean)
if (!implReports.length) throw new Error('所有 implementer 都失敗或被跳過,管線中止')

// ---- Phase 2: Review / fix loop(review gate:high/medium 修完才放行,上限 2 輪修復)----
const FINDINGS_SCHEMA = {
  type: 'object',
  required: ['findings'],
  properties: {
    findings: {
      type: 'array',
      items: {
        type: 'object',
        required: ['severity', 'file', 'summary', 'fix'],
        properties: {
          severity: { type: 'string', enum: ['high', 'medium', 'low'] },
          file: { type: 'string' },
          line: { type: 'number' },
          summary: { type: 'string' },
          fix: { type: 'string' },
        },
      },
    },
  },
}

function agentForFile(file) {
  if (file.startsWith('frontend/')) return 'frontend-implementer'
  if (file.startsWith('workflow/')) return 'python-implementer'
  return 'dotnet-implementer' // platform/、backend/ 與其他 .NET 檔
}

const MAX_FIX_ROUNDS = 2
let unresolved = []
let reviewRounds = 0
for (let round = 0; ; round++) {
  const review = await agent(
    `審查目前工作樹的未提交變更(用 git status/diff 自行取得範圍)是否正確實作規格、符合跨服務契約與併發規約。規格如下:\n${spec}\n\n實作回報:\n${implReports.join('\n---\n')}\n\n只回報經查證的問題;severity=low 表示可延後的建議。`,
    { agentType: 'code-reviewer', label: `review:r${round + 1}`, phase: 'Review', schema: FINDINGS_SCHEMA }
  )
  reviewRounds = round + 1
  const all = (review && review.findings) || []
  unresolved = all.filter(f => f.severity !== 'low')
  log(`Review 第 ${reviewRounds} 輪:${all.length} 項發現,需修復(high/medium)${unresolved.length} 項`)
  if (!unresolved.length || round >= MAX_FIX_ROUNDS) break

  // 按檔案前綴路由回對應 implementer,並行修復
  const groups = {}
  for (const f of unresolved) {
    const t = agentForFile(f.file)
    if (!groups[t]) groups[t] = []
    groups[t].push(f)
  }
  await parallel(
    Object.entries(groups).map(([type, fs]) => () =>
      agent(
        `修復以下 code review 發現(修根因,不是繞過症狀;修完把受影響測試跑到全綠並回報):\n${JSON.stringify(fs, null, 2)}`,
        { agentType: type, label: `fix:r${round + 1}:${type}`, phase: 'Review' }
      )
    )
  )
}
if (unresolved.length) {
  log(`警告:仍有 ${unresolved.length} 項 high/medium 發現未在 ${MAX_FIX_ROUNDS} 輪內修復,列入最終回報,人工裁決`)
}

// ---- Phase 3: Simplify(行為保持)----
phase('Simplify')
const simplifyReport = await agent(
  '對目前工作樹的未提交變更做行為保持的簡化(清晰度、重用既有 helper、移除 dead flexibility)。不改任何行為;完成後把受影響測試跑到全綠,回報做了什麼與測試結果。',
  { agentType: 'code-simplifier', label: 'simplify', phase: 'Simplify' }
)

// ---- Phase 4: Verify(僅跨服務變更)----
let verifyReport = null
if (args && args.crossService) {
  phase('Verify')
  verifyReport = await agent(
    `跨服務行為已變更,依你預載的 verify-phase 程序選擇並執行對應的驗證(compose 全鏈或 D 階段 verifier)。變更規格:\n${spec}\n回報驗證結果與失敗細節。`,
    { agentType: 'e2e-verifier', label: 'verify', phase: 'Verify' }
  )
} else {
  log('crossService 未設,跳過 e2e 驗證(單服務變更由各自測試套件覆蓋)')
}

// ---- Phase 5: Docs ----
let docsReport = null
if (!args || args.docs !== false) {
  phase('Docs')
  docsReport = await agent(
    `功能變更已完成並通過審查。同步 README/AGENTS/docs 中受影響的結構、指令、契約與注意事項(遵守單一事實來源:契約全文只在 docs/,AGENTS.md 只留不變量+連結)。規格:\n${spec}`,
    { agentType: 'docs-updater', label: 'docs', phase: 'Docs' }
  )
} else {
  log('docs:false,跳過文件同步')
}

return {
  implementers: implementers.map(([type, list]) => ({ agent: type, areas: list })),
  implReports,
  reviewRounds,
  unresolvedFindings: unresolved,
  simplifyReport,
  verifyReport,
  docsReport,
}
