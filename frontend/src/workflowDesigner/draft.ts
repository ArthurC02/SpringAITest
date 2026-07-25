import type { WorkflowDraft, WorkflowKind, WorkflowRuntimeVariant } from '../types'

export function createBlankDraft(
  kind: WorkflowKind,
  runtimeVariant: WorkflowRuntimeVariant,
): WorkflowDraft {
  return {
    definition: {
      schemaVersion: 1,
      kind,
      ...(kind === 'agent-runtime' ? { runtimeVariant } : {}),
      nodes: [],
      edges: [],
      governance: { maxSteps: 40, maxConcurrency: 4 },
    },
    ui_metadata: {
      positions: {},
      viewport: { x: 0, y: 0, zoom: 1 },
      groups: {},
      collapsed: [],
    },
  }
}
