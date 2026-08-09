export const meta = {
  name: 'remediate',
  description: 'Resumable remediation: consume unchecked plan-file items in waves, tick as they land, review at the end',
  whenToUse:
    'Bulk remediation of an audit plan file (plans/<topic>-<date>.md with "- [ ]" checkbox findings). args: {planPath, waveSize?:number}. Resume after any interruption by re-invoking with the same planPath — unchecked items are the remaining work.',
  phases: [
    { title: 'Load', detail: 'parse unchecked checkbox items from the plan file' },
    { title: 'Fix', detail: 'waves (default 8) of tier-modeled fix agents; checkbox ticks checkpoint each wave' },
    { title: 'Review', detail: 'code-reviewer over the accumulated working-tree changes' },
  ],
}

if (!args || !args.planPath) throw new Error('args.planPath 必填:plans/ 下含 "- [ ]" checkbox 的整治計畫檔')
const planPath = args.planPath
const WAVE_SIZE = (args && args.waveSize) || 8

// ---- Phase 1: Load(便宜模型讀計畫檔,回傳未勾項)----
phase('Load')
const ITEMS_SCHEMA = {
  type: 'object',
  required: ['items'],
  properties: {
    items: {
      type: 'array',
      items: {
        type: 'object',
        required: ['id', 'text'],
        properties: {
          id: { type: 'string', description: '該項在計畫檔中可唯一定位的短識別(例如行首編號或 file:line)' },
          text: { type: 'string', description: 'checkbox 項的完整文字(含 file:line 與修法)' },
          file: { type: 'string', description: '該項主要涉及的檔案路徑(repo 相對),無法判定則省略' },
          tier: { type: 'string', enum: ['mechanical', 'standard', 'architectural'] },
        },
      },
    },
  },
}
const loaded = await agent(
  `讀取 ${planPath},列出所有「- [ ]」未勾選的整治項(已勾「- [x]」的跳過)。每項給:可唯一定位的 id、完整文字、主要涉及檔案、複雜度分級(mechanical=改名/搬移/機械式清理;standard=單模組修復/重構;architectural=跨模組設計、併發、安全)。只讀檔,不做任何修改。`,
  { label: 'load-plan', phase: 'Load', model: 'haiku', effort: 'low', schema: ITEMS_SCHEMA }
)
const items = (loaded && loaded.items) || []
if (!items.length) return { done: true, message: `計畫檔沒有未勾選項,無事可做:${planPath}` }
log(`載入 ${items.length} 個未勾選整治項`)

// ---- Phase 2: Fix(分波 + 每波勾選 checkpoint + 預算守門)----
function agentTypeFor(file) {
  const f = (file || '').replace(/\\/g, '/')
  if (f.startsWith('frontend/')) return 'frontend-implementer'
  if (f.startsWith('workflow/')) return 'python-implementer'
  return 'dotnet-implementer' // platform/、backend/ 與未標檔案的預設
}
// 分級模型:mechanical 降到 sonnet(覆寫 agent 定義的 opus);其餘用定義釘的模型
function modelFor(tier) {
  return tier === 'mechanical' ? 'sonnet' : undefined
}

const waves = []
for (let i = 0; i < items.length; i += WAVE_SIZE) waves.push(items.slice(i, i + WAVE_SIZE))

const completed = []
const failed = []
phase('Fix')
for (let w = 0; w < waves.length; w++) {
  if (budget.total && budget.remaining() < 50000) {
    log(`token 預算不足(剩 ${Math.round(budget.remaining() / 1000)}k),停在 wave ${w}/${waves.length} 邊界;未勾項即剩餘工作,同 planPath 重跑即續`)
    break
  }
  const wave = waves[w]
  log(`Wave ${w + 1}/${waves.length}:${wave.length} 項`)
  const results = await parallel(
    wave.map(it => () =>
      agent(
        `整治項(出自 ${planPath}):\n${JSON.stringify(it, null, 2)}\n修根因而非症狀;修完後 grep 兄弟路徑確認同缺陷不存在或一併修;把受影響測試跑到全綠。不要修改計畫檔本身。完成回報:變更檔案、測試結果。`,
        { agentType: agentTypeFor(it.file), model: modelFor(it.tier), label: `fix:${it.id}`, phase: 'Fix' }
      ).then(r => ({ item: it, report: r }))
    )
  )
  const ok = results.filter(Boolean)
  const ko = wave.filter(it => !ok.some(r => r.item.id === it.id))
  completed.push(...ok)
  failed.push(...ko)
  if (ok.length) {
    await agent(
      `在 ${planPath} 中,把以下 id 對應的「- [ ]」勾成「- [x]」(只動 checkbox 字元,不改其他內容):\n${ok.map(r => r.item.id).join('\n')}`,
      { label: `tick:wave${w + 1}`, phase: 'Fix', model: 'haiku', effort: 'low' }
    )
  }
  if (ko.length) log(`Wave ${w + 1}:${ko.length} 項失敗/被跳過,保持未勾,重跑時自動重試`)
}

// ---- Phase 3: Review(整批一次審,發現列入回報,人工或 dev-cycle 跟進)----
phase('Review')
const FINDINGS_SCHEMA = {
  type: 'object',
  required: ['findings'],
  properties: {
    findings: {
      type: 'array',
      items: {
        type: 'object',
        required: ['severity', 'file', 'summary'],
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
const review = completed.length
  ? await agent(
      `審查目前工作樹的未提交變更(整治批次,來源計畫:${planPath})。重點:修復是否只治症狀、兄弟路徑是否漏修、併發/鎖序/型別安全回歸。只回報經查證的問題。`,
      { agentType: 'code-reviewer', label: 'review', phase: 'Review', schema: FINDINGS_SCHEMA }
    )
  : { findings: [] }

return {
  planPath,
  totalUnchecked: items.length,
  completed: completed.map(r => r.item.id),
  failedOrSkipped: failed.map(it => it.id),
  reviewFindings: review.findings,
  resume: failed.length || completed.length < items.length
    ? `同 planPath 重跑 remediate 即續作剩餘未勾項`
    : '全部完成',
}
