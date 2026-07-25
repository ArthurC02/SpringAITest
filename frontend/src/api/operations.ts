import { apiFetch } from './http'
export const getOperationsMetrics = () => apiFetch<Record<string, unknown>>('/api/admin/operations/metrics')
export const getVersionComparison = () => apiFetch<Record<string, unknown>>('/api/admin/operations/version-comparison')
export const getLegacyInventory = () => apiFetch<unknown[]>('/api/admin/operations/legacy-inventory')
