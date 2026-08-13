import type { WorkflowDefinition, WorkflowGraphNode, WorkflowNodeType, WorkflowUiMetadata } from '../types'
import { catalogForKind } from './catalog'
import { canConnect } from './connection'
import { newNodeId } from './editing'

export interface ClipboardPayload {
  nodes: WorkflowGraphNode[]
  /** 只保留兩端都在選取範圍內的邊。 */
  edges: { source: { nodeId: string; port: string }; target: { nodeId: string; port: string } }[]
  positions: Record<string, { x: number; y: number }>
}

/**
 * ponytail: 內部剪貼簿（module 層變數），只在同一個瀏覽器分頁的同一次 session 內有效。
 * 跨分頁貼上要走系統剪貼簿 JSON，需剪貼簿權限且必須把外來 JSON 當不可信輸入完整驗證，
 * 超出本波範圍；需要時再加：寫入時 `navigator.clipboard.writeText`，讀取時先過 schema 驗證再走同一組 fail-closed 過濾。
 */
let buffer: ClipboardPayload | null = null
export const readClipboard = () => buffer
export const writeClipboard = (payload: ClipboardPayload | null) => { buffer = payload }

export function copySelection(
  definition: WorkflowDefinition, metadata: WorkflowUiMetadata, nodeIds: string[],
): ClipboardPayload | null {
  const picked = new Set(nodeIds)
  const nodes = definition.nodes.filter((node) => picked.has(node.id))
  if (nodes.length === 0) return null
  return {
    nodes,
    edges: definition.edges
      .filter((edge) => picked.has(edge.source.nodeId) && picked.has(edge.target.nodeId))
      .map((edge) => ({ source: edge.source, target: edge.target })),
    positions: Object.fromEntries(nodes.map((node) => [node.id, metadata.positions[node.id] ?? { x: 0, y: 0 }])),
  }
}

/** 貼上偏移的預設值（取不到滑鼠游標時）。 */
const PASTE_OFFSET = 40

/**
 * fail-closed 貼上：不在目前 kind/variant 目錄中的節點型別直接丟棄；同型別的必要階段
 * （`requiredStage`）已存在時也丟棄（伺服器 validator 回 `duplicate_required_stage`，
 * 貼出來必定驗證失敗）；重映射後的邊逐條過 `canConnect`（逐條累加，maxConnections 才算得準）。
 * `at` 為畫布座標時，貼上內容的左上角對齊該點；否則整體 +40/+40。
 */
export function pasteClipboard(
  definition: WorkflowDefinition, metadata: WorkflowUiMetadata, payload: ClipboardPayload,
  catalog: WorkflowNodeType[], at: { x: number; y: number } | null,
): { definition: WorkflowDefinition; ui_metadata: WorkflowUiMetadata; ids: string[] } | null {
  const allowed = new Map(catalogForKind(catalog, definition.kind, definition.runtimeVariant)
    .map((type) => [`${type.type}@${type.version}`, type]))
  // 必要階段以「型別」計數（validator 也是），目前圖裡缺席時仍可貼回。
  const takenStages = new Set(definition.nodes.map((node) => node.type))
  const kept: WorkflowGraphNode[] = []
  for (const node of payload.nodes) {
    const type = allowed.get(`${node.type}@${node.typeVersion}`)
    if (!type) continue
    if (type.requiredStage) {
      if (takenStages.has(node.type)) continue
      takenStages.add(node.type)
    }
    kept.push(node)
  }
  if (kept.length === 0) return null

  const idMap = new Map(kept.map((node) => [node.id, newNodeId()]))
  const origin = kept.map((node) => payload.positions[node.id] ?? { x: 0, y: 0 })
  const dx = at ? at.x - Math.min(...origin.map((p) => p.x)) : PASTE_OFFSET
  const dy = at ? at.y - Math.min(...origin.map((p) => p.y)) : PASTE_OFFSET

  const positions = { ...metadata.positions }
  let next: WorkflowDefinition = {
    ...definition,
    nodes: [...definition.nodes, ...kept.map((node) => ({ ...node, id: idMap.get(node.id)!, config: { ...node.config } }))],
  }
  for (const node of kept) {
    const from = payload.positions[node.id] ?? { x: 0, y: 0 }
    positions[idMap.get(node.id)!] = { x: from.x + dx, y: from.y + dy }
  }
  for (const edge of payload.edges) {
    const source = idMap.get(edge.source.nodeId)
    const target = idMap.get(edge.target.nodeId)
    if (!source || !target) continue
    const connection = { source, sourceHandle: edge.source.port, target, targetHandle: edge.target.port }
    if (!canConnect(connection, next, catalog)) continue
    next = { ...next, edges: [...next.edges, {
      id: crypto.randomUUID(),
      source: { nodeId: source, port: edge.source.port },
      target: { nodeId: target, port: edge.target.port },
    }] }
  }
  return { definition: next, ui_metadata: { ...metadata, positions }, ids: [...idMap.values()] }
}
