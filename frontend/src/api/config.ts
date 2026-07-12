import { apiFetch } from './http'
import type { ConfigEntry } from '../types'

export function listConfig(): Promise<ConfigEntry[]> {
  return apiFetch<ConfigEntry[]>('/api/config')
}

/** 更新單一設定；非 ADMIN 後端回 403（前端 UI 已隱藏入口，此為第二道防線）。 */
export function updateConfig(key: string, value: string): Promise<ConfigEntry> {
  return apiFetch<ConfigEntry>(`/api/config/${encodeURIComponent(key)}`, {
    method: 'PUT',
    body: JSON.stringify({ value }),
  })
}
