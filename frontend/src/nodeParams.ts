// Configuration Set 七個開放鍵的 UI metadata + 純前端範圍檢查（設計 §9）。
// 這裡是 UI 常數（非 API 契約），與 NodeParamsTab 表單和 selfcheck 共用；不含 JSX，可被 node 直跑。
import type { ConfigKey, ConfigurationValues } from './types'

// ponytail: 白名單硬編對齊 infra/litellm-config.yaml 的 chat 模型（gpt-4o-mini / mock-gpt）;
//           前端無端點可查已配置模型,改模型時同步這裡。text-embedding 不是 chat 模型故不列。
const MODEL_OPTIONS = ['gpt-4o-mini', 'mock-gpt'] as const

interface ConfigFieldDef {
  key: ConfigKey
  label: string
  kind: 'int' | 'float' | 'select'
  min?: number
  max?: number
  step?: number
  options?: readonly string[]
  /** 全域預設值（僅作 placeholder 提示；未覆寫時後端回落此值，不隨表單送出）。 */
  default: number | string
}

export const CONFIG_FIELDS: ConfigFieldDef[] = [
  { key: 'retrieval.top_k', label: '檢索筆數', kind: 'int', min: 1, max: 50, default: 4 },
  { key: 'kb_query.top_k', label: '知識庫查詢筆數', kind: 'int', min: 1, default: 8 },
  { key: 'kb_query.max_retrieval_attempts', label: '最大檢索重試次數', kind: 'int', min: 1, default: 2 },
  { key: 'workflow.timeout_seconds', label: '工作流逾時（秒）', kind: 'int', min: 1, default: 120 },
  { key: 'llm.model', label: '語言模型', kind: 'select', options: MODEL_OPTIONS, default: 'gpt-4o-mini' },
  { key: 'intent.confidence_threshold', label: '意圖信心門檻', kind: 'float', min: 0, max: 1, step: 0.05, default: 0.6 },
  { key: 'llm.temperature', label: '生成溫度', kind: 'float', min: 0, max: 2, step: 0.1, default: 0.7 },
]

/**
 * 表單草稿（字串）→ ConfigurationValues：只收有值的鍵（未填 = 不覆寫），
 * select 存字串、數值鍵轉 number。
 */
export function draftToValues(draft: Record<string, string>): ConfigurationValues {
  const out: ConfigurationValues = {}
  for (const f of CONFIG_FIELDS) {
    const raw = draft[f.key]
    if (raw === undefined || raw.trim() === '') continue
    out[f.key] = f.kind === 'select' ? raw : Number(raw)
  }
  return out
}

/**
 * 純前端範圍/型別檢查（UX；真正把關在後端 422 + fieldErrors）。
 * 回傳 key→中文錯誤;空物件 = 通過。只檢查有值的鍵。
 */
export function validateConfigValues(values: ConfigurationValues): Record<string, string> {
  const errors: Record<string, string> = {}
  for (const f of CONFIG_FIELDS) {
    const v = values[f.key]
    if (v === undefined || v === '') continue
    if (f.kind === 'select') {
      if (!f.options!.includes(String(v))) errors[f.key] = `請從清單選擇一個有效的${f.label}。`
      continue
    }
    const n = typeof v === 'number' ? v : Number(v)
    if (!Number.isFinite(n)) {
      errors[f.key] = `${f.label}必須是數字。`
      continue
    }
    if (f.kind === 'int' && !Number.isInteger(n)) {
      errors[f.key] = `${f.label}必須是整數。`
      continue
    }
    if (f.min !== undefined && n < f.min) {
      errors[f.key] = `${f.label}不可小於 ${f.min}。`
      continue
    }
    if (f.max !== undefined && n > f.max) {
      errors[f.key] = `${f.label}不可大於 ${f.max}。`
    }
  }
  return errors
}
