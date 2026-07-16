/** 答案欄位鍵（依優先序）：answerOf 取主答案、SkillRunPanel 排除為 chip，同一組單一來源避免漂移。 */
export const ANSWER_KEYS: readonly string[] = ['business_result', 'final_answer', 'answer', 'summary', 'reply']

/** 從 output 取一個字串型答案欄位（既有欄位 fallback 解析）。原 WorkflowsView 搬來。 */
export function answerOf(output: Record<string, unknown>): string | null {
  for (const k of ANSWER_KEYS) {
    const v = output[k]
    if (typeof v === 'string') return v
  }
  return null
}
