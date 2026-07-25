import type { Edge, Node, Viewport } from '@xyflow/react'
import type { WorkflowDefinition, WorkflowNodeType, WorkflowUiMetadata } from '../types'

export interface CanvasNodeData extends Record<string, unknown> {
  title: string
  required: boolean
  invalid?: boolean
  traceStatus?: string
  inputs: string[]
  outputs: string[]
}

/** Projection only: React Flow state never becomes the execution definition. */
export function toCanvas(
  definition: WorkflowDefinition,
  metadata: WorkflowUiMetadata,
  catalog: WorkflowNodeType[],
  invalidIds: Set<string> = new Set(),
  trace: Map<string, string> = new Map(),
): { nodes: Node<CanvasNodeData>[]; edges: Edge[] } {
  return {
    nodes: definition.nodes.map((item) => ({
      id: item.id,
      type: 'workflow',
      position: metadata.positions[item.id] ?? { x: 0, y: 0 },
      data: (() => {
        const type = catalog.find((entry) => entry.type === item.type && entry.version === item.typeVersion)
        return { title: type?.title ?? `${item.type}@${item.typeVersion}`, required: !!type?.requiredStage,
          inputs: type?.inputs.map((port) => port.id) ?? [], outputs: type?.outputs.map((port) => port.id) ?? [],
          invalid: invalidIds.has(item.id), traceStatus: trace.get(item.id) }
      })(),
    })),
    edges: definition.edges.map((edge) => ({
      id: edge.id,
      source: edge.source.nodeId,
      sourceHandle: edge.source.port,
      target: edge.target.nodeId,
      targetHandle: edge.target.port,
      label: `${edge.source.port} → ${edge.target.port}`,
    })),
  }
}

export function patchPositions(metadata: WorkflowUiMetadata, nodes: Node[]): WorkflowUiMetadata {
  const positions = { ...metadata.positions }
  for (const node of nodes) positions[node.id] = { x: node.position.x, y: node.position.y }
  return { ...metadata, positions }
}

export function patchViewport(metadata: WorkflowUiMetadata, viewport: Viewport): WorkflowUiMetadata {
  return { ...metadata, viewport: { x: viewport.x, y: viewport.y, zoom: viewport.zoom } }
}

/** Useful for tests and previews: UI-only metadata is deliberately absent. */
export function semanticFingerprint(definition: WorkflowDefinition): string {
  return JSON.stringify(definition)
}
