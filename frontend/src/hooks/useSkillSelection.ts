import { useCallback, useEffect, useRef, useState } from 'react'
import { getBusinessWorkflow } from '../api/businessWorkflows'
import { getSkill, listSkillCatalog } from '../api/skills'
import {
  isActiveSkillRequest,
  isCurrentSkillRequest,
  isSynchronizedSkillSnapshot,
} from '../skills/revision'
import type { Skill, SkillKind } from '../types'
import type { SkillRow } from './useSkillRows'

export type SkillSub = 'edit' | 'run' | 'history'
export type SelectedSkill = Pick<SkillRow, 'name' | 'source' | 'schema' | 'kind' | 'revision'>

interface Options {
  kind: SkillKind
  reload: () => Promise<unknown>
  invalidateEditors: () => void
  notify: (message: string, type: 'success' | 'error') => void
}

/** 兩入口共用的選取、非同步世代與 restore 原子快照狀態機。 */
export function useSkillSelection({ kind, reload, invalidateEditors, notify }: Options) {
  const [selected, setSelected] = useState<SelectedSkill | null>(null)
  const [sub, setSub] = useState<SkillSub>('edit')
  const [restorePending, setRestorePending] = useState(false)
  const mountedRef = useRef(false)
  const generationRef = useRef(0)
  const restorePendingRef = useRef(false)

  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      generationRef.current += 1
      restorePendingRef.current = false
    }
  }, [])

  const reset = useCallback(() => {
    generationRef.current += 1
    setSelected(null)
    setSub('edit')
    invalidateEditors()
  }, [invalidateEditors])

  const open = useCallback((row: SkillRow, nextSub: SkillSub) => {
    if (restorePendingRef.current) return
    generationRef.current += 1
    invalidateEditors()
    setSelected({
      name: row.name,
      source: row.source,
      schema: row.schema,
      kind: row.kind,
      revision: row.revision,
    })
    setSub(nextSub)
  }, [invalidateEditors])

  const beginEdit = useCallback((row: SelectedSkill) => {
    if (restorePendingRef.current) return null
    const generation = ++generationRef.current
    setSelected(row)
    setSub('edit')
    return generation
  }, [])

  const isRequestActive = useCallback((generation: number) =>
    isActiveSkillRequest(mountedRef.current, generation, generationRef.current)
      && !restorePendingRef.current, [])

  const navigateSub = useCallback((nextSub: SkillSub) => {
    if (restorePendingRef.current) return
    generationRef.current += 1
    invalidateEditors()
    setSub(nextSub)
  }, [invalidateEditors])

  const onRestorePendingChange = useCallback((pending: boolean) => {
    if (!mountedRef.current) return
    restorePendingRef.current = pending
    setRestorePending(pending)
    if (pending) {
      generationRef.current += 1
      invalidateEditors()
    }
  }, [invalidateEditors])

  const onHistoryReverted = useCallback(async (restored: Skill) => {
    const generation = ++generationRef.current
    const expectedRevision = restored.current_revision
    const active = () => isActiveSkillRequest(mountedRef.current, generation, generationRef.current)
    try {
      let snapshot: { detail: Skill; entry: Awaited<ReturnType<typeof listSkillCatalog>>[number] } | null = null
      for (let attempt = 0; attempt < 3; attempt += 1) {
        if (!active()) return false
        const [detail, catalog] = await Promise.all([
          restored.kind === 'flow' ? getBusinessWorkflow(restored.name) : getSkill(restored.name),
          listSkillCatalog(),
        ])
        if (!active()) return false
        const entry = catalog.find((item) => item.name === restored.name)
        if (entry && isSynchronizedSkillSnapshot(expectedRevision, detail, entry)) {
          snapshot = { detail, entry }
          break
        }
      }
      if (!active()) return false
      if (!snapshot) throw new Error('能力已回溯，但最新版本資料尚未同步，請返回清單後重新開啟。')
      const { detail, entry } = snapshot
      if (!isCurrentSkillRequest(generation, generationRef.current, expectedRevision, detail.current_revision)) {
        return false
      }
      if (detail.kind !== kind) {
        restorePendingRef.current = false
        setRestorePending(false)
        reset()
        await reload()
        notify(
          detail.kind === 'agentic'
            ? '已回溯為 Agent Skill，請切換到「Agent Skills」分頁繼續。'
            : '已回溯為業務流程，請切換到「業務流程」分頁繼續。',
          'success',
        )
        return false
      }
      setSelected((current) => current?.name === restored.name ? {
        ...current,
        kind: detail.kind,
        schema: entry.input_schema ?? null,
        revision: detail.current_revision,
      } : current)
      invalidateEditors()
      await reload()
      return active()
    } catch (error) {
      if (!active()) return false
      restorePendingRef.current = false
      setRestorePending(false)
      notify((error as Error).message, 'error')
      reset()
      await reload()
      return false
    }
  }, [invalidateEditors, kind, notify, reload, reset])

  return {
    selected,
    sub,
    restorePending,
    open,
    reset,
    beginEdit,
    isRequestActive,
    navigateSub,
    onRestorePendingChange,
    onHistoryReverted,
  }
}
