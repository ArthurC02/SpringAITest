import type { RunSummaryItem } from './types'

/** O2 種類中文標籤(規格 §「種類」)。未列舉值原樣顯示,不擋新種類。 */
const KIND_LABEL: Record<string, string> = {
  'direct-agent': 'Direct',
  worker: 'Worker',
  verifier: 'Verifier',
  orchestrator: '協作 root',
}
export function kindLabel(kind: string): string {
  return KIND_LABEL[kind] ?? kind
}

/** 沿用 AgentTestConsole 的狀態語彙(見 agentRunDisplay.ts 週邊),O2 額外聯集 timed_out。 */
const STATUS_LABEL: Record<string, string> = {
  queued: '排隊中',
  starting: '啟動中',
  running: '執行中',
  waiting_input: '等待補充資訊',
  waiting_approval: '等待核准',
  completed: '已完成',
  failed: '失敗',
  cancelled: '已取消',
  timed_out: '已逾時',
}
export function statusLabel(status: string): string {
  return STATUS_LABEL[status] ?? status
}

const STATUS_CHIP_CLASS: Record<string, string> = {
  queued: 'chip--processing',
  starting: 'chip--processing',
  running: 'chip--processing',
  waiting_input: 'chip--warn',
  waiting_approval: 'chip--warn',
  completed: 'chip--ready',
  failed: 'chip--failed',
  cancelled: 'chip--skip',
  timed_out: 'chip--failed',
}
/** 未列舉值一律用中性樣式,不假裝知道是成功還是失敗。 */
export function statusChipClass(status: string): string {
  return STATUS_CHIP_CLASS[status] ?? 'chip--skip'
}

/** 截短 id + r 版本(比照 ApprovalInbox 的 agentIdentity),root 列改顯示 Orchestrator 身分。 */
export function runIdentity(run: RunSummaryItem): string {
  const id = run.kind === 'orchestrator' ? run.orchestratorId : run.agentId
  const revision = run.kind === 'orchestrator' ? run.orchestratorRevision : run.agentRevision
  if (!id) return '—'
  const short = id.slice(0, 8)
  return revision !== null ? `${short} · r${revision}` : short
}

/**
 * elapsed_seconds 人話化:取最大兩個單位,不到一分鐘就用秒。
 * ponytail: 不做完整 i18n duration 函式庫,這個畫面只需要粗略人話,夠用就好。
 */
export function fmtElapsed(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '—'
  const s = Math.floor(seconds)
  if (s < 60) return `${s} 秒`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m} 分鐘`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h} 小時${m % 60 ? ` ${m % 60} 分` : ''}`
  const d = Math.floor(h / 24)
  return `${d} 天${h % 24 ? ` ${h % 24} 小時` : ''}`
}

/** 協作 root 列的子任務摘要(規格「child_progress 摘要」)。其餘 kind 沒有 child_progress。 */
export function childProgressSummary(run: RunSummaryItem): string | null {
  const p = run.childProgress
  if (!p) return null
  return `共 ${p.total}(排隊 ${p.queued}‧執行中 ${p.running}‧完成 ${p.completed}‧失敗 ${p.failed}‧取消 ${p.cancelled})`
}
