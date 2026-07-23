import type { Skill, SkillCatalogEntry, SkillRevision } from '../types'

/**
 * Flow revision 只靠 definition 即可回復；agentic revision 必須仍保存歷史 package。
 * 判斷刻意只看該 revision 自己的 kind，不能拿 current skill kind 代替（歷史可能跨類型）。
 */
export function canRestoreRevision(
  revision: Pick<SkillRevision, 'kind' | 'has_package'>,
): boolean {
  return revision.kind === 'flow' || revision.has_package === true
}

/** 非同步結果只有 generation 與預期 revision 都沒過期時才能寫回 UI。 */
export function isCurrentSkillRequest(
  requestGeneration: number,
  currentGeneration: number,
  expectedRevision: number,
  actualRevision: number,
): boolean {
  return requestGeneration === currentGeneration && expectedRevision === actualRevision
}

/** Component 已卸載或 generation 已前進時，非同步工作必須立即停止且不產生 UI 副作用。 */
export function isActiveSkillRequest(
  mounted: boolean,
  requestGeneration: number,
  currentGeneration: number,
): boolean {
  return mounted && requestGeneration === currentGeneration
}

/**
 * Restore 後的 detail 與 catalog 必須描述同一個 revision，才可把 catalog schema
 * 與 detail identity 組成一份 UI snapshot；任何一方舊版都整份丟棄。
 */
export function isSynchronizedSkillSnapshot(
  expectedRevision: number,
  detail: Pick<Skill, 'name' | 'current_revision' | 'kind'>,
  catalog: Pick<SkillCatalogEntry, 'name' | 'revision' | 'kind'>,
): boolean {
  const detailKind = detail.kind ?? 'flow'
  const catalogKind = catalog.kind ?? 'flow'
  return (
    detail.name === catalog.name
    && detail.current_revision === expectedRevision
    && catalog.revision === expectedRevision
    && detailKind === catalogKind
  )
}
