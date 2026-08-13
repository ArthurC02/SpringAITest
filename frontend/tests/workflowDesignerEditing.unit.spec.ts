import { expect, test } from 'vitest'
import type { WorkflowDefinition, WorkflowDraft, WorkflowNodeType, WorkflowUiMetadata } from '../src/types'
import {
  HISTORY_LIMIT, historyPush, historyRedo, historyReset, historyUndo, isViewportOnlyChange,
} from '../src/workflowDesigner/history'
import { copySelection, pasteClipboard } from '../src/workflowDesigner/clipboard'
import { addConnectedNode, insertNodeOnEdge } from '../src/workflowDesigner/editing'

// n8n 式操作體感的純邏輯核心：undo/redo 歷史、剪貼簿重映射、邊上中插的相容性判斷。
// 三者都不碰 React Flow，故在 vitest 直接驗；fail-closed 規則（catalogForKind / canConnect）
// 必須在這一層就成立，UI 只是入口。

const catalog: WorkflowNodeType[] = [
  { type: 'start', version: '1.0', title: 'Start', kind: 'control', inputs: [], outputs: [{ id: 'out', dataType: 'control' }], configSchema: {}, authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'run.control', workflowKinds: ['orchestrator'], requiredStage: true },
  { type: 'optional', version: '1.0', title: 'Optional', kind: 'control', inputs: [{ id: 'in', dataType: 'control' }], outputs: [{ id: 'out', dataType: 'control' }], configSchema: {}, authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'run.control', workflowKinds: ['orchestrator'], requiredStage: false },
  { type: 'sink', version: '1.0', title: 'Sink', kind: 'control', inputs: [{ id: 'in', dataType: 'control', maxConnections: 1 }], outputs: [], configSchema: {}, authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'run.control', workflowKinds: ['orchestrator'], requiredStage: false },
  { type: 'data_only', version: '1.0', title: 'Data Only', kind: 'data', inputs: [{ id: 'in', dataType: 'data' }], outputs: [{ id: 'out', dataType: 'data' }], configSchema: {}, authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'run.control', workflowKinds: ['orchestrator'], requiredStage: false },
  { type: 'runtime_only', version: '1.0', title: 'Runtime Only', kind: 'control', inputs: [{ id: 'in', dataType: 'control' }], outputs: [{ id: 'out', dataType: 'control' }], configSchema: {}, authoringCapability: 'workflow.manage', catalogVisibility: 'system-admin', runtimePolicy: 'run.control', workflowKinds: ['agent-runtime'], requiredStage: false },
]

const definition: WorkflowDefinition = {
  schemaVersion: 1, kind: 'orchestrator', governance: {},
  nodes: [
    { id: 'a', type: 'start', typeVersion: '1.0', config: {} },
    { id: 'b', type: 'optional', typeVersion: '1.0', config: { retries: 2 } },
    { id: 'c', type: 'sink', typeVersion: '1.0', config: {} },
  ],
  edges: [
    { id: 'e1', source: { nodeId: 'a', port: 'out' }, target: { nodeId: 'b', port: 'in' } },
    { id: 'e2', source: { nodeId: 'b', port: 'out' }, target: { nodeId: 'c', port: 'in' } },
  ],
}
const metadata: WorkflowUiMetadata = {
  positions: { a: { x: 0, y: 0 }, b: { x: 100, y: 50 }, c: { x: 200, y: 100 } },
  viewport: { x: 0, y: 0, zoom: 1 },
}
const draft: WorkflowDraft = { definition, ui_metadata: metadata }
const withNodes = (ids: string[]): WorkflowDraft => ({
  definition: { ...definition, nodes: definition.nodes.filter((node) => ids.includes(node.id)) },
  ui_metadata: metadata,
})

test.describe('Undo/redo history reducer', () => {
  test('pure viewport changes replace present without entering the undo stack', () => {
    const panned: WorkflowDraft = { definition, ui_metadata: { ...metadata, viewport: { x: 40, y: 12, zoom: 1.4 } } }
    expect(isViewportOnlyChange(draft, panned)).toBe(true)
    const state = historyPush(historyReset(draft), panned)
    expect(state.past).toEqual([])
    expect(state.present).toBe(panned)
    // 位置拖曳不是 viewport 變更，必須進歷史。
    const moved: WorkflowDraft = { definition, ui_metadata: { ...metadata, positions: { ...metadata.positions, a: { x: 9, y: 9 } } } }
    expect(isViewportOnlyChange(draft, moved)).toBe(false)
    expect(historyPush(state, moved).past).toHaveLength(1)
  })

  test('an identical snapshot is not a history entry (drag stop re-sends the same positions)', () => {
    const state = historyReset(draft)
    expect(historyPush(state, { definition: { ...definition }, ui_metadata: { ...metadata } })).toBe(state)
  })

  test('undo and redo move the pointer without losing the other side', () => {
    const one = historyPush(historyReset(draft), withNodes(['a', 'b']))
    const two = historyPush(one, withNodes(['a']))
    const back = historyUndo(two)
    expect(back.present.definition.nodes.map((node) => node.id)).toEqual(['a', 'b'])
    expect(back.future).toHaveLength(1)
    expect(historyUndo(back).present).toBe(draft)
    expect(historyRedo(back).present).toBe(two.present)
    // 空堆疊時是 no-op，不得丟例外也不得改變狀態。
    expect(historyUndo(historyReset(draft))).toEqual(historyReset(draft))
    expect(historyRedo(historyReset(draft))).toEqual(historyReset(draft))
  })

  test('coalesced pushes merge into one entry and still drop the redo branch', () => {
    const base = historyReset(draft)
    // 堆疊還空的時候仍要先留下基準線，否則第一次微調就 undo 不回去。
    expect(historyPush(base, withNodes(['a', 'b']), true).past).toHaveLength(1)
    const first = historyPush(base, withNodes(['a', 'b']))
    const merged = historyPush(first, withNodes(['a', 'c']), true)
    expect(merged.past).toHaveLength(first.past.length)
    expect(merged.future).toEqual([])
  })

  test('a coalesced push right after undo starts a new entry instead of eating the redo branch', () => {
    const two = historyPush(historyPush(historyReset(draft), withNodes(['a', 'b'])), withNodes(['a', 'c']))
    const undone = historyUndo(two)
    const nudged = historyPush(undone, withNodes(['a']), true)
    // 合併會讓這次移動沒有 undo 點：再按一次 Ctrl+Z 就跳過兩步。必須自己留一筆。
    expect(nudged.past).toHaveLength(undone.past.length + 1)
    expect(nudged.future).toEqual([])
    expect(historyUndo(nudged).present).toBe(undone.present)
  })

  test('the stack truncates at HISTORY_LIMIT and drops the oldest snapshot', () => {
    let state = historyReset(draft)
    for (let index = 0; index < HISTORY_LIMIT + 5; index += 1) {
      state = historyPush(state, {
        definition, ui_metadata: { ...metadata, positions: { ...metadata.positions, a: { x: index, y: index } } },
      })
    }
    expect(state.past).toHaveLength(HISTORY_LIMIT)
    // 最舊的那筆（原始草稿）已被丟掉，剩下的最舊是第 6 次推入前的狀態。
    expect(state.past[0].ui_metadata.positions.a).toEqual({ x: 4, y: 4 })
  })
})

test.describe('Clipboard copy/paste remapping', () => {
  test('copy keeps only edges whose two endpoints are both selected', () => {
    const payload = copySelection(definition, metadata, ['a', 'b'])!
    expect(payload.nodes.map((node) => node.id)).toEqual(['a', 'b'])
    expect(payload.edges).toEqual([{ source: { nodeId: 'a', port: 'out' }, target: { nodeId: 'b', port: 'in' } }])
    expect(payload.positions).toEqual({ a: { x: 0, y: 0 }, b: { x: 100, y: 50 } })
    expect(copySelection(definition, metadata, [])).toBeNull()
  })

  test('paste assigns fresh ids, remaps internal edges and offsets by 40/40 without a cursor', () => {
    // b/c 都不是必要階段（a 是 start，複製它會被必要階段規則丟掉，那是另一條測試的判準）。
    const payload = copySelection(definition, metadata, ['b', 'c'])!
    const result = pasteClipboard(definition, metadata, payload, catalog, null)!
    expect(result.ids).toHaveLength(2)
    expect(result.ids.some((id) => ['a', 'b', 'c'].includes(id))).toBe(false)
    expect(result.definition.nodes).toHaveLength(5)
    // 內部邊保留一條，且兩端都指向新 id。
    const added = result.definition.edges.filter((edge) => !['e1', 'e2'].includes(edge.id))
    expect(added).toHaveLength(1)
    expect(result.ids).toContain(added[0].source.nodeId)
    expect(result.ids).toContain(added[0].target.nodeId)
    expect(result.ui_metadata.positions[result.ids[0]]).toEqual({ x: 140, y: 90 })
    // config 是複本，不與來源共用參考。
    const pasted = result.definition.nodes.find((node) => node.id === result.ids[0])!
    expect(pasted.config).toEqual({ retries: 2 })
    expect(pasted.config).not.toBe(definition.nodes[1].config)
  })

  test('paste drops a required stage the graph already has, but restores an absent one', () => {
    // a 是 requiredStage 的 start：再貼一個必定被 validator 的 duplicate_required_stage 擋下。
    const payload = copySelection(definition, metadata, ['a', 'b'])!
    const result = pasteClipboard(definition, metadata, payload, catalog, null)!
    expect(result.ids).toHaveLength(1)
    expect(result.definition.nodes.filter((node) => node.type === 'start')).toHaveLength(1)
    // 被丟掉的節點的邊也不重建（來源邊 a→b 的一端已不存在）。
    expect(result.definition.edges).toHaveLength(2)
    // 圖裡沒有該必要階段時仍貼得回去，否則刪不掉又補不回來。
    const without = { ...definition, nodes: definition.nodes.filter((node) => node.type !== 'start'), edges: [] }
    const restored = pasteClipboard(without, metadata, payload, catalog, null)!
    expect(restored.ids).toHaveLength(2)
    expect(restored.definition.nodes.filter((node) => node.type === 'start')).toHaveLength(1)
  })

  test('paste anchors the top-left of the pasted block at the cursor position', () => {
    const payload = copySelection(definition, metadata, ['b', 'c'])!
    const result = pasteClipboard(definition, metadata, payload, catalog, { x: 500, y: 300 })!
    const positions = result.ids.map((id) => result.ui_metadata.positions[id])
    expect(positions).toContainEqual({ x: 500, y: 300 })
    expect(positions).toContainEqual({ x: 600, y: 350 })
  })

  test('paste drops node types outside the current kind catalog and never re-adds their edges', () => {
    const payload = copySelection(
      { ...definition, nodes: [...definition.nodes, { id: 'r', type: 'runtime_only', typeVersion: '1.0', config: {} }],
        edges: [...definition.edges, { id: 'e3', source: { nodeId: 'b', port: 'out' }, target: { nodeId: 'r', port: 'in' } }] },
      metadata, ['b', 'r'],
    )!
    expect(payload.nodes).toHaveLength(2)
    const result = pasteClipboard(definition, metadata, payload, catalog, null)!
    expect(result.ids).toHaveLength(1)
    expect(result.definition.nodes.some((node) => node.type === 'runtime_only')).toBe(false)
    expect(result.definition.edges).toHaveLength(2)
    // 目錄整個不含來源型別時，貼上完全不發生。
    expect(pasteClipboard(definition, metadata, payload, [], null)).toBeNull()
  })

  test('a remapped edge that fails canConnect is dropped while its nodes still paste', () => {
    // sink.in 上限 1 條：複製 b→c 後貼上，新的 b'→c' 仍是空 port 故成立；
    // 但把 target 指向既有的 c（已被 e2 佔滿）就必須被丟棄。
    const payload = copySelection(definition, metadata, ['b', 'c'])!
    const crossing = { ...payload, edges: [...payload.edges, { source: { nodeId: 'b', port: 'out' }, target: { nodeId: 'c', port: 'in' } }] }
    const result = pasteClipboard(definition, metadata, crossing, catalog, null)!
    expect(result.ids).toHaveLength(2)
    // 兩條來源邊只留下一條（重複的那條被 maxConnections 擋掉）。
    expect(result.definition.edges).toHaveLength(3)
  })
})

test.describe('Edge insert compatibility', () => {
  test('inserting a compatible node replaces the edge with two connected edges', () => {
    const optional = catalog[1]
    const next = insertNodeOnEdge(definition, 'e2', optional, catalog, 'n1')!
    expect(next.edges.some((edge) => edge.id === 'e2')).toBe(false)
    expect(next.nodes.map((node) => node.id)).toEqual(['a', 'b', 'c', 'n1'])
    const added = next.edges.filter((edge) => edge.id !== 'e1')
    expect(added.map((edge) => [edge.source.nodeId, edge.target.nodeId])).toEqual([['b', 'n1'], ['n1', 'c']])
  })

  test('an incompatible dataType or a missing edge fails closed with null', () => {
    expect(insertNodeOnEdge(definition, 'e2', catalog[3], catalog, 'n1')).toBeNull()
    expect(insertNodeOnEdge(definition, 'missing', catalog[1], catalog, 'n1')).toBeNull()
    // start 沒有 input port，兩段中的第一段就接不上。
    expect(insertNodeOnEdge(definition, 'e2', catalog[0], catalog, 'n1')).toBeNull()
  })

  test('drag-to-empty adds the node and only wires it when canConnect agrees', () => {
    const connected = addConnectedNode(definition, { nodeId: 'a', port: 'out' }, catalog[1], catalog, 'n1')
    expect(connected.connected).toBe(true)
    expect(connected.definition.edges.at(-1)).toMatchObject({ source: { nodeId: 'a', port: 'out' }, target: { nodeId: 'n1', port: 'in' } })
    const mismatch = addConnectedNode(definition, { nodeId: 'a', port: 'out' }, catalog[3], catalog, 'n2')
    expect(mismatch.connected).toBe(false)
    expect(mismatch.definition.nodes.some((node) => node.id === 'n2')).toBe(true)
    expect(mismatch.definition.edges).toHaveLength(2)
  })
})
