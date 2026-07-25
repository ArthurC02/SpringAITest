import { apiFetch } from './http'
import type { ChatOrchestrator } from '../types'

export async function listChatOrchestrators(): Promise<ChatOrchestrator[]> {
  const value = await apiFetch<unknown>('/api/chat/orchestrators')
  if (!value || typeof value !== 'object') return []
  const items = (value as { orchestrators?: unknown }).orchestrators
  if (!Array.isArray(items)) return []
  return items.flatMap((item) => {
    if (!item || typeof item !== 'object') return []
    const row = item as Record<string, unknown>
    if (
      typeof row.id !== 'string'
      || typeof row.name !== 'string'
      || typeof row.description !== 'string'
      || typeof row.revision !== 'number'
      || !Array.isArray(row.capabilities)
      || !row.capabilities.every((capability) => typeof capability === 'string')
    ) return []
    return [{
      id: row.id,
      name: row.name,
      description: row.description,
      revision: row.revision,
      capabilities: row.capabilities as string[],
    }]
  })
}
