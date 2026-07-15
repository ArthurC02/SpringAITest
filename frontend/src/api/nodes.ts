import { apiFetch } from './http'
import type { NodeInfo } from '../types'

/** 節點目錄：程式即事實來源，無 CRUD（唯讀契約瀏覽）。 */
export function listNodes(): Promise<NodeInfo[]> {
  return apiFetch<NodeInfo[]>('/api/nodes')
}
