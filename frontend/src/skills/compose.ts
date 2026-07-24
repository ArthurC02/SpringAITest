import type { SkillForm } from './templates'

// 純字串 patch，不 parse/emit YAML（縫①：前端無 YAML 庫）。骨架用兩種 sentinel：
//   1. 規則注入槽：block literal（`|`）內獨佔一行的 __RULE_SLOT__。規則槽現在一律是
//      nl_logic 的 instruction block literal，故只會出現在區塊純量內。
//   2. 白名單純量覆寫：帶尾註 `# __SLOT_<field>__` 的行（純量槽用 YAML `key: value`）。
// compose 只做定點字串替換，骨架本身是啟動即合法/可載入的 YAML。

/** 只有這些 field 允許被簡單模式覆寫；topK 走數字，其餘走字串。 */
const WHITELIST: (keyof SkillForm)[] = ['name', 'description', 'topK']
const NUMERIC_FIELDS = new Set<keyof SkillForm>(['topK'])

/** rule 留空時的安全 no-op：nl_logic 照原樣回答（保持骨架 valid）。 */
function emptyRule(): string {
  return '照原樣回答'
}

function indentOf(line: string): string {
  const m = /^(\s*)/.exec(line)
  return m ? m[1] : ''
}

function toScalar(field: keyof SkillForm, value: string): string {
  if (NUMERIC_FIELDS.has(field)) {
    // 數字原樣；非法數字（理論上被 number input 擋掉）退回加引號，避免破壞 YAML。
    return Number.isFinite(Number(value)) ? String(Number(value)) : JSON.stringify(value)
  }
  // 字串加雙引號並跳脫：YAML double-quoted 與 JSON 字串跳脫規則相容。
  return JSON.stringify(value)
}

export function compose(form: SkillForm, baseDefinition: string): string {
  const out: string[] = []
  for (const line of baseDefinition.split('\n')) {
    // 1) 規則注入槽：整行換成 rule，逐行套骨架 slot 行的縮排（區塊純量對多行內容字面安全）。
    if (line.includes('__RULE_SLOT__')) {
      const indent = indentOf(line)
      const rawRule = (form.rule ?? '').trim() === '' ? emptyRule() : form.rule!
      out.push(
        rawRule
          .split('\n')
          .map((l) => (l.length > 0 ? indent + l : ''))
          .join('\n'),
      )
      continue
    }

    // 2) 白名單純量覆寫：只重寫使用者提供且允許的欄位，保留 key 與尾註 sentinel。
    let patched = line
    for (const field of WHITELIST) {
      const value = form[field]
      if (value == null || value === '') continue
      if (!patched.includes(`__SLOT_${field}__`)) continue
      // 分隔符只認冒號（YAML `key: value`）；純量槽全是 YAML 賦值，無 Python `=` 槽。
      const re = new RegExp(`^(\\s*[^:\\n]+:\\s*)(.*?)(\\s*#\\s*__SLOT_${field}__.*)$`)
      patched = patched.replace(re, `$1${toScalar(field, value)}$3`)
    }
    out.push(patched)
  }
  return out.join('\n')
}
