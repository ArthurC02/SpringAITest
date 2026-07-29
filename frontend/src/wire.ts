// API 回應正規化的共用原語。後端 snake_case/camelCase 並存（見根 AGENTS.md），
// 所以每個欄位都用 pick() 逐一嘗試別名，而不是假設單一命名。
export type JsonObject = Record<string, unknown>

/** 陣列與 null 都不是可取欄位的 object，一律退回空物件。 */
export function object(value: unknown): JsonObject {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? (value as JsonObject)
    : {}
}

/** 依序取第一個存在的鍵（含值為 null/undefined 的情況）。 */
export function pick(source: JsonObject, ...keys: string[]): unknown {
  for (const key of keys) {
    if (Object.hasOwn(source, key)) return source[key]
  }
  return undefined
}

export function text(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null
}

/**
 * 遮罩防線用的字串投影：超出長度上限就整段捨棄（回 null），而不是截斷後照樣顯示。
 * 用於 trace 這種「只允許短標識字串」的白名單投影。
 */
export function shortText(value: unknown, maxLength = 160): string | null {
  const parsed = text(value)
  return parsed !== null && parsed.length <= maxLength ? parsed : null
}

export function integer(value: unknown): number | null {
  return typeof value === 'number' && Number.isSafeInteger(value) ? value : null
}

/** 允許小數(如 cost_units)；非有限數一律視為缺席，不硬轉成 0。 */
export function number(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}
