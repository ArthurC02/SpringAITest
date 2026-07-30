import { apiFetch, apiFetchBlob } from './http'
import type { Skill, SkillInfo, SkillSimpleForm, SkillValidation } from '../types'

/** Business Workflow 是宣告式 YAML 的管理面；DTO 仍沿用同一儲存 artifact 型別。 */
export function listBusinessWorkflows(): Promise<SkillInfo[]> {
  return apiFetch<SkillInfo[]>('/api/business-workflows')
}

export function getBusinessWorkflow(name: string): Promise<Skill> {
  return apiFetch<Skill>(`/api/business-workflows/${encodeURIComponent(name)}`)
}

export function createBusinessWorkflow(definition: string, simpleForm?: SkillSimpleForm): Promise<void> {
  return apiFetch<void>('/api/business-workflows', { method: 'POST', body: JSON.stringify(simpleForm ? { definition, simpleForm } : { definition }) })
}

export function updateBusinessWorkflow(name: string, definition: string, simpleForm?: SkillSimpleForm): Promise<void> {
  return apiFetch<void>(`/api/business-workflows/${encodeURIComponent(name)}`, { method: 'PUT', body: JSON.stringify(simpleForm ? { definition, simpleForm } : { definition }) })
}

export function deleteBusinessWorkflow(name: string): Promise<void> {
  return apiFetch<void>(`/api/business-workflows/${encodeURIComponent(name)}`, { method: 'DELETE' })
}

export function validateBusinessWorkflow(definition: string): Promise<SkillValidation> {
  return apiFetch<SkillValidation>('/api/business-workflows/validate', { method: 'POST', body: JSON.stringify({ definition }) })
}

export async function exportBusinessWorkflow(name: string): Promise<void> {
  const blob = await apiFetchBlob(`/api/business-workflows/${encodeURIComponent(name)}/export`)
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = `${name}.zip`
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}
