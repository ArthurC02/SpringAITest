// O5 排程觸發的顯示字典與輸入轉換(比照 runDiscoveryDisplay.ts 的慣例:
// 已知值給中文,未知值一律原樣顯示,不擋後端之後新增的列舉值)。

const TRIGGER_STATUS_LABEL: Record<string, string> = {
  scheduled: '已排程',
  cancelled: '已取消',
  fired: '已觸發',
  misfired: '已錯過',
  failed: '失敗',
}
export function triggerStatusLabel(status: string): string {
  return TRIGGER_STATUS_LABEL[status] ?? status
}

const TRIGGER_STATUS_CHIP_CLASS: Record<string, string> = {
  scheduled: 'chip--processing',
  cancelled: 'chip--skip',
  fired: 'chip--ready',
  misfired: 'chip--warn',
  failed: 'chip--failed',
}
/** 未列舉值一律中性樣式,不假裝知道是成功還是失敗。 */
export function triggerStatusChipClass(status: string): string {
  return TRIGGER_STATUS_CHIP_CLASS[status] ?? 'chip--skip'
}

/**
 * fire ledger 的 delivery status:後端 `TriggerOccurrenceStatuses` 的 11 個 bounded 值,
 * 失敗/略過的原因就併在 `skipped_*`/`failed_*` 前綴裡(後端刻意不另開原因碼欄位),
 * 所以中文對照直接掛在同一份 status 上,未知值原樣顯示。
 */
const OCCURRENCE_STATUS_LABEL: Record<string, string> = {
  pending: '待處理',
  claimed: '已認領',
  fired: '已觸發',
  skipped_cancelled: '已略過(觸發器已取消)',
  skipped_misfired: '已略過(超過補觸發時限)',
  failed_principal_unavailable: '失敗(建立者身分不可用)',
  failed_target_missing: '失敗(找不到目標協作流程)',
  failed_target_unpublished: '失敗(目標協作流程未發布)',
  failed_target_revision_changed: '失敗(目標版本已變更)',
  failed_dispatch_disabled: '失敗(多 Agent 協作派工未啟用)',
  failed_root_rejected: '失敗(目標拒絕建立執行)',
}
export function occurrenceStatusLabel(status: string): string {
  return OCCURRENCE_STATUS_LABEL[status] ?? status
}

/** id 截短(比照 runDiscoveryDisplay.runIdentity 的 8 碼慣例)。 */
export function shortId(id: string | null): string {
  return id ? id.slice(0, 8) : '—'
}

/**
 * `<input type="datetime-local">` 的值是「沒有時區的本地時刻」;送出前轉成絕對 UTC ISO
 * (計畫 §6:one-shot 用 UTC 絕對時刻,timezone 只在輸入層轉換)。
 * 空字串或無法解析一律回 null,由呼叫端顯示欄位錯誤,不送出猜測值。
 */
export function localInputToUtcIso(value: string): string | null {
  if (!value.trim()) return null
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? null : date.toISOString()
}
