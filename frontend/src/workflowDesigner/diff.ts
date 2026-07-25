import type { WorkflowDefinition } from '../types'

export interface SemanticDiffSummary { added: string[]; removed: string[]; changed: string[] }

/** Diff only Graph IR, never positions/viewport/groups. Stable node IDs make this deterministic. */
export function semanticDiff(current: WorkflowDefinition, previous: WorkflowDefinition): SemanticDiffSummary {
  const now = new Map(current.nodes.map((node) => [node.id, node]))
  const old = new Map(previous.nodes.map((node) => [node.id, node]))
  return {
    added: [...now.keys()].filter((id) => !old.has(id)),
    removed: [...old.keys()].filter((id) => !now.has(id)),
    changed: [...now.keys()].filter((id) => old.has(id) && JSON.stringify(now.get(id)) !== JSON.stringify(old.get(id))),
  }
}
