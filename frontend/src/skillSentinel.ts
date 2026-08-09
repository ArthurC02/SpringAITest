/**
 * 聊天串流命中 skill 路由時，platform 會在內容通道尾端追加一個 sentinel
 * （`\n<!--skill:{skillName}-->`，skillName 符合 `[a-z0-9-]+`），不新增 SSE 欄位
 * （見 docs/cross-service-contracts.md、SkillRoutingAgent.cs）。這裡負責在渲染前
 * 剝離尾端 sentinel 取出來源徽章文字；剝離失敗（無標記／標記在中段／格式不符）
 * 一律 fail open，回傳原始內容、不顯示徽章。
 */
const TRAILING_SKILL_SENTINEL = /\n<!--skill:([a-z0-9-]+)-->$/

export interface StrippedSkillContent {
  content: string
  skillName: string | null
}

export function stripSkillSentinel(content: string): StrippedSkillContent {
  const match = TRAILING_SKILL_SENTINEL.exec(content)
  if (!match) return { content, skillName: null }
  return { content: content.slice(0, match.index), skillName: match[1] }
}

const KNOWLEDGE_BASE_SKILLS = new Set(['rag-qa', 'kb-query'])

/**
 * 徽章顯示文字：知識庫類 skill 統一顯示「來源:知識庫」；名單外一律用通用文案
 * 「來源:自訂技能」，不外洩租戶自訂的技術 slug（管理者取的英文 kebab 名稱）。
 */
export function skillSourceLabel(skillName: string): string {
  return KNOWLEDGE_BASE_SKILLS.has(skillName) ? '來源:知識庫' : '來源:自訂技能'
}
