/**
 * 答案欄位鍵（依優先序）：answerOf 取主答案、SkillRunPanel 排除為 chip，同一組單一來源避免漂移。
 * 與 Platform SkillRoutingAgent.OutputKeys 對齊（business_result, answer, final_answer, report, summary），
 * agentic runner 寫固定 public `answer` 鍵 → 兩邊清單不再漂移。
 * ponytail: 去掉 Platform 未 honor 的 `reply`；`answer` 提前到與 Platform 同優先序。
 */
export const ANSWER_KEYS: readonly string[] = ['business_result', 'answer', 'final_answer', 'report', 'summary']

/** 從 output 取一個字串型答案欄位（既有欄位 fallback 解析）。原 WorkflowsView 搬來。 */
export function answerOf(output: Record<string, unknown>): string | null {
  for (const k of ANSWER_KEYS) {
    const v = output[k]
    if (typeof v === 'string') return v
  }
  return null
}
