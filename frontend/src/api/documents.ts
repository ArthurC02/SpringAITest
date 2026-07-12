import { apiFetch } from './http'
import type { DocumentInfo } from '../types'

/** POST /api/documents 的 202 回應，只含這三欄。 */
export interface CreatedDocument {
  id: string
  title: string
  status: DocumentInfo['status']
}

export function listDocuments(): Promise<DocumentInfo[]> {
  return apiFetch<DocumentInfo[]>('/api/documents')
}

/** 建立文件：後端回 202 Accepted（非同步處理），實際入庫由 backend consumer 完成。 */
export function createDocument(title: string, text: string): Promise<CreatedDocument> {
  return apiFetch<CreatedDocument>('/api/documents', {
    method: 'POST',
    body: JSON.stringify({ title, text }),
  })
}

export function deleteDocument(id: string): Promise<void> {
  return apiFetch<void>(`/api/documents/${encodeURIComponent(id)}`, { method: 'DELETE' })
}
