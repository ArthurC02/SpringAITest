import type { WorkflowKind, WorkflowNodeType, WorkflowRuntimeVariant } from '../types'

/** Missing applicability metadata is fail-closed; D4 catalogs must explicitly name supported kinds. */
export function catalogForKind(
  catalog: WorkflowNodeType[],
  kind: WorkflowKind,
  runtimeVariant?: WorkflowRuntimeVariant,
): WorkflowNodeType[] {
  return catalog.filter((node) => {
    if (!Array.isArray(node.workflowKinds) || !node.workflowKinds.includes(kind)) return false
    if (kind !== 'agent-runtime' || !Array.isArray(node.runtimeVariants)) return true
    return runtimeVariant != null && node.runtimeVariants.includes(runtimeVariant)
  })
}

export function isRequiredStage(catalog: WorkflowNodeType[], type: string, kind: WorkflowKind): boolean {
  return catalogForKind(catalog, kind).some((node) => node.type === type && node.requiredStage === true)
}

/** Deletion is an explicit allow: stale/missing catalog metadata and unknown versions stay locked. */
export function canDeleteNode(
  catalog: WorkflowNodeType[],
  kind: WorkflowKind,
  type: string,
  version: string,
  runtimeVariant?: WorkflowRuntimeVariant,
): boolean {
  const match = catalogForKind(catalog, kind, runtimeVariant).find(
    (node) => node.type === type && node.version === version,
  )
  return match?.requiredStage === false
}
