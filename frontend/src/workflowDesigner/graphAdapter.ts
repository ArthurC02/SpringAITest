import type { Edge, Node, Viewport } from '@xyflow/react'
import type { WorkflowDefinition, WorkflowNodeType, WorkflowUiMetadata } from '../types'
import { canDeleteNode } from './catalog'

export interface CanvasNodeData extends Record<string, unknown> {
  title: string
  required: boolean
  invalid?: boolean
  traceStatus?: string
  inputs: string[]
  outputs: string[]
  /** `type@version`，節點卡片次行顯示。 */
  typeLabel: string
  /** catalog kind，決定左上圖示方塊。 */
  nodeKind: string
  /** 由 catalog 推導（canDeleteNode 唯一判準），供 hover 工具列/右鍵選單顯示可否刪除。 */
  deletable: boolean
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
          invalid: invalidIds.has(item.id), traceStatus: trace.get(item.id),
          typeLabel: `${item.type}@${item.typeVersion}`, nodeKind: type?.kind ?? 'control',
          deletable: canDeleteNode(catalog, definition.kind, item.type, item.typeVersion, definition.runtimeVariant) }
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

/** props → 本地 React Flow 投影的同步：語意欄位一律以新投影為準，
 * 只保留 UI-only 的本地狀態(選取高亮、已量測尺寸)，避免語意提交後選取跳掉、邊閃爍。
 * `selectId` 指定時(如剛新增/剛貼上的節點)改為獨佔選取這些節點。 */
export function syncCanvasNodes<T extends Node>(next: T[], prev: T[], selectId: string | string[] | null = null): T[] {
  const byId = new Map(prev.map((node) => [node.id, node]))
  const wanted = selectId === null ? null : new Set(Array.isArray(selectId) ? selectId : [selectId])
  return next.map((node) => {
    const old = byId.get(node.id)
    return { ...node, selected: wanted ? wanted.has(node.id) : !!old?.selected, measured: old?.measured }
  })
}

export function syncCanvasEdges(next: Edge[], prev: Edge[]): Edge[] {
  const byId = new Map(prev.map((edge) => [edge.id, edge]))
  return next.map((edge) => ({ ...edge, selected: !!byId.get(edge.id)?.selected }))
}

export function patchPositions(
  metadata: WorkflowUiMetadata,
  nodes: { id: string; position: { x: number; y: number } }[],
): WorkflowUiMetadata {
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
