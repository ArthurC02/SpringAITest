import { useCallback } from 'react'
import { listSkillCatalog } from '../api/skills'
import type { SkillCatalogEntry, SkillInfo, SkillInputField, SkillKind, SkillSimpleForm } from '../types'
import { useResource } from './useResource'

export interface SkillRow {
  name: string
  description: string
  required_role: string
  source: 'custom' | 'builtin'
  revision: number | null
  enabled: boolean
  schema: Record<string, SkillInputField> | null
  kind: SkillKind
  simpleForm: SkillSimpleForm | null
}

/** 管理清單與統一 catalog 的單一合併點；API kind 是唯一 discriminator。 */
export function useSkillRows(kind: SkillKind, listCustom: () => Promise<SkillInfo[]>) {
  const fetchRows = useCallback(async (): Promise<SkillRow[]> => {
    const [customs, catalog] = await Promise.all([listCustom(), listSkillCatalog()])
    const schemaOf = (name: string): Record<string, SkillInputField> | null =>
      catalog.find((entry) => entry.name === name)?.input_schema ?? null
    const customRows = customs
      .filter((skill) => skill.kind === kind)
      .map((skill): SkillRow => ({
        name: skill.name,
        description: skill.description,
        required_role: skill.required_role,
        source: 'custom',
        revision: skill.current_revision,
        enabled: skill.enabled,
        schema: schemaOf(skill.name),
        kind: skill.kind,
        simpleForm: skill.simpleForm ?? null,
      }))
    const builtinRows = catalog
      .filter((entry: SkillCatalogEntry) =>
        entry.source === 'builtin' && entry.kind === kind && !entry.name.startsWith('template-'))
      .map((entry): SkillRow => ({
        name: entry.name,
        description: entry.description,
        required_role: entry.required_role,
        source: 'builtin',
        revision: entry.revision,
        enabled: true,
        schema: entry.input_schema ?? null,
        kind: entry.kind,
        simpleForm: null,
      }))
    return [...customRows, ...builtinRows]
  }, [kind, listCustom])

  const resource = useResource(fetchRows)
  return { ...resource, rows: resource.data ?? [] }
}
