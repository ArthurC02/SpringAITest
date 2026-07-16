// Configuration Set CRUD 代理（platform → backend，ADMIN-only + tenant-scoped）。
// 全經 apiFetch（帶 Bearer、統一 ApiError、401 全域登出）；欄位 snake_case。
import { apiFetch } from './http'
import type { ConfigurationSet, ConfigurationSetInfo, ConfigurationValues } from '../types'

const BASE = '/api/configuration-sets'

/** 本租戶所有 Configuration Set（含 active 標記）。 */
export function listConfigurationSets(): Promise<ConfigurationSetInfo[]> {
  return apiFetch<ConfigurationSetInfo[]>(BASE)
}

/** 單筆（含 values）—— 編輯前載入完整值。 */
export function getConfigurationSet(id: string): Promise<ConfigurationSet> {
  return apiFetch<ConfigurationSet>(`${BASE}/${encodeURIComponent(id)}`)
}

// 建立/更新只帶 {name, values}；is_active 走專屬 activate 端點，不由 upsert 帶。
// 回應形狀不保證，寫入後一律重讀清單，不依賴回應 body（比照 skills.ts）。
export function createConfigurationSet(
  name: string,
  values: ConfigurationValues,
): Promise<void> {
  return apiFetch<void>(BASE, { method: 'POST', body: JSON.stringify({ name, values }) })
}

export function updateConfigurationSet(
  id: string,
  name: string,
  values: ConfigurationValues,
): Promise<void> {
  return apiFetch<void>(`${BASE}/${encodeURIComponent(id)}`, {
    method: 'PUT',
    body: JSON.stringify({ name, values }),
  })
}

export function deleteConfigurationSet(id: string): Promise<void> {
  return apiFetch<void>(`${BASE}/${encodeURIComponent(id)}`, { method: 'DELETE' })
}

/** 啟用（後端原子把本租戶其餘組 is_active=false，DB 部分唯一索引保「至多一 active」）。 */
export function activateConfigurationSet(id: string): Promise<void> {
  return apiFetch<void>(`${BASE}/${encodeURIComponent(id)}/activate`, { method: 'POST' })
}
