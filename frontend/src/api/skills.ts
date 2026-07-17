import { apiFetch, apiFetchBlob } from './http'
import type {
  Skill,
  SkillCatalogEntry,
  SkillInfo,
  SkillResult,
  SkillRevision,
  SkillValidation,
} from '../types'

/** 可執行的 skill 清單（內建 + 租戶自訂，帶 source/revision）。執行分頁用。 */
export function listSkillCatalog(): Promise<SkillCatalogEntry[]> {
  return apiFetch<SkillCatalogEntry[]>('/api/skills/catalog')
}

/** 執行 skill。回應是 {skill, output}（原 workflow 執行端點已退役）。 */
export function invokeSkill(
  name: string,
  input: Record<string, unknown>,
): Promise<SkillResult> {
  return apiFetch<SkillResult>(`/api/skills/${encodeURIComponent(name)}/invoke`, {
    method: 'POST',
    body: JSON.stringify({ input }),
  })
}

/** 管理用清單（ADMIN）。軟刪後的 skill 不在此清單。 */
export function listSkills(): Promise<SkillInfo[]> {
  return apiFetch<SkillInfo[]>('/api/skills')
}

export function getSkill(name: string): Promise<Skill> {
  return apiFetch<Skill>(`/api/skills/${encodeURIComponent(name)}`)
}

/** 唯讀歷史（依 revision 遞減）。 */
export function listSkillRevisions(name: string): Promise<SkillRevision[]> {
  return apiFetch<SkillRevision[]>(`/api/skills/${encodeURIComponent(name)}/revisions`)
}

// body 只有 YAML 原文；name/description/required_role 由後端從 definition 解析（唯一事實來源）。
// 回應形狀契約只保證 PUT 回 {revision}（AT4-03），因此存檔後一律重讀，不依賴回應 body。
// 建立時名稱已存在 → 409。
export function createSkill(definition: string): Promise<void> {
  return apiFetch<void>('/api/skills', {
    method: 'POST',
    body: JSON.stringify({ definition }),
  })
}

/** 更新既有 skill → 產生新 revision；路由的 name 即身分，YAML 內 name 不符 → 422。 */
export function updateSkill(name: string, definition: string): Promise<void> {
  return apiFetch<void>(`/api/skills/${encodeURIComponent(name)}`, {
    method: 'PUT',
    body: JSON.stringify({ definition }),
  })
}

/** 停用（後端為軟刪 enabled=false，revision 保留供稽核）。 */
export function deleteSkill(name: string): Promise<void> {
  return apiFetch<void>(`/api/skills/${encodeURIComponent(name)}`, { method: 'DELETE' })
}

/** 匯出既有 skill 為 zip（SKILL.md + skill.yaml）並觸發瀏覽器下載。走 apiFetchBlob 保留 Bearer/401 行為。 */
export async function exportSkill(name: string): Promise<void> {
  const blob = await apiFetchBlob(`/api/skills/${encodeURIComponent(name)}/export`)
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = `${name}.zip`
  document.body.appendChild(a)
  a.click()
  a.remove()
  // ponytail: 同一 tick 撤銷會偶發打斷下載，延遲 1s 撤銷。
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

/** 引擎級靜態驗證;無副作用,編輯器即時校驗用。valid=false 也是 HTTP 200。 */
export function validateSkill(definition: string): Promise<SkillValidation> {
  return apiFetch<SkillValidation>('/api/skills/validate', {
    method: 'POST',
    body: JSON.stringify({ definition }),
  })
}
