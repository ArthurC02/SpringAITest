import type { SkillValidation } from '../types'

/** 引擎錯誤碼 → 人話（規格 §3.4）。未知碼直接顯示原碼，不吞掉。 */
export const CODE_LABEL: Record<string, string> = {
  unknown_node: '引用了不存在的節點或版本',
  unknown_tool: '引用了不存在的 Tool',
  unbounded_loop: 'loop 缺少 max_iterations 或超出 1~10',
  invalid_expression: '條件式含白名單外的語法（僅允許 state.<鍵>、比較、and/or/not、in）',
  // ponytail: 單一 forbidden_script 碼涵蓋所有 script 沙箱違規（.append()/lambda/清單推導式/while/import…），
  // 給非技術使用者可照做的替代寫法，避開 AST/引擎術語（SSR-P2B-007）。
  forbidden_script:
    '程式規則用了不支援的寫法（例如 .append()、lambda、清單推導式、while 迴圈或 import）；請改用簡單賦值搭配 sorted()，例如「結果 = 結果 + [新項目]」。',
  dataflow_error: '資料流警告：讀取了無前置步驟產出的鍵',
  invalid_flow: 'flow 為空、步驟型別未知，或 YAML 解析失敗',
}

// dataflow_error 是警告級：不阻擋存檔（規格 §3.4）。其餘皆為阻擋級。
export const WARN_CODES = new Set(['dataflow_error'])

/** 阻擋級錯誤(排除警告碼);存檔判斷的單一事實來源。 */
export const blockingErrors = (v: SkillValidation) => v.errors.filter((e) => !WARN_CODES.has(e.code))
