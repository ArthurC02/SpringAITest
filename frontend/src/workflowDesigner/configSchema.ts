/**
 * Node Config 表單化(P1 Phase A'):把 catalog 提供的 `configSchema`（見
 * `workflow/app/orchestration/catalog.py` `_node()` 的 `wire()` 實際輸出)解析成
 * 「鍵/型別/必填」可枚舉的欄位清單，驅動 `WorkflowDesigner` 的泛型表單。
 *
 * ponytail: 只支援目前 Harness catalog 實際會產生的窄子集——頂層 object、
 * `properties` 全為 string/integer/number/boolean 基本型別，且每個屬性只用
 * type/minimum/maximum 三個關鍵字(catalog 目前只有 `bounded_agent_loop` 等三個
 * node type 帶 `{maxIterations|maxRepairRounds: integer, minimum:1}`，其餘皆為
 * 空 schema)。任何超出此子集的形狀(巢狀 object/array、enum、oneOf/anyOf……)
 * 一律回傳 null，呼叫端退回原始 JSON textarea 逃生口 —— fail-open，不臆測未知形狀；
 * 之後 catalog 若長出更豐富的 schema，這裡再擴充支援的關鍵字/型別。
 */
export interface WorkflowNodeConfigField {
  key: string
  type: 'string' | 'integer' | 'number' | 'boolean'
  required: boolean
  minimum?: number
  maximum?: number
}

const SUPPORTED_TYPES = new Set(['string', 'integer', 'number', 'boolean'])
const KNOWN_PROPERTY_KEYWORDS = new Set(['type', 'minimum', 'maximum'])

export function parseConfigSchema(schema: Record<string, unknown> | undefined | null): WorkflowNodeConfigField[] | null {
  if (!schema || typeof schema !== 'object') return null
  if (schema.type !== undefined && schema.type !== 'object') return null
  const properties = schema.properties
  if (properties === undefined) return []
  if (typeof properties !== 'object' || properties === null || Array.isArray(properties)) return null
  const requiredRaw = schema.required
  const required = new Set(Array.isArray(requiredRaw) ? requiredRaw.filter((r): r is string => typeof r === 'string') : [])

  const fields: WorkflowNodeConfigField[] = []
  for (const [key, rawProp] of Object.entries(properties as Record<string, unknown>)) {
    if (typeof rawProp !== 'object' || rawProp === null || Array.isArray(rawProp)) return null
    const prop = rawProp as Record<string, unknown>
    const propType = prop.type
    if (typeof propType !== 'string' || !SUPPORTED_TYPES.has(propType)) return null
    if (Object.keys(prop).some((k) => !KNOWN_PROPERTY_KEYWORDS.has(k))) return null
    const minimum = prop.minimum
    const maximum = prop.maximum
    fields.push({
      key,
      type: propType as WorkflowNodeConfigField['type'],
      required: required.has(key),
      minimum: typeof minimum === 'number' ? minimum : undefined,
      maximum: typeof maximum === 'number' ? maximum : undefined,
    })
  }
  return fields
}
