import {
  useCallback, useEffect, useMemo, useRef, useState,
  type DragEvent, type MouseEvent as ReactMouseEvent,
} from 'react'
import {
  Background, BackgroundVariant, Controls, MarkerType, MiniMap, Panel, ReactFlow, ReactFlowProvider, SelectionMode,
  applyEdgeChanges, applyNodeChanges, useEdgesState, useNodesState, useReactFlow, useViewport,
  type Connection, type Edge, type EdgeChange, type Node, type NodeChange, type OnConnect, type OnConnectEnd,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import type {
  WorkflowDefinition, WorkflowNodeType, WorkflowSimulation, WorkflowUiMetadata, WorkflowValidation,
  WorkflowTraceEntry,
} from '../types'
import { canConnect } from './connection'
import { parseConfigSchema, type WorkflowNodeConfigField } from './configSchema'
import { patchPositions, patchViewport, syncCanvasEdges, syncCanvasNodes, toCanvas, type CanvasNodeData } from './graphAdapter'
import { autoLayout } from './layout'
import WorkflowNode from './WorkflowNode'
import WorkflowEdge from './WorkflowEdge'
import NodePicker from './NodePicker'
import ContextMenu, { type ContextMenuItem } from './ContextMenu'
import Modal from '../components/Modal'
import { canDeleteNode, catalogForKind } from './catalog'
import {
  addConnectedNode, insertNodeOnEdge, newNodeId, reconnectSemanticEdge, removeSemanticEdges, removeSemanticNodes,
} from './editing'
import { copySelection, pasteClipboard, readClipboard, writeClipboard } from './clipboard'
import { isTextEntryTarget } from './keyboard'
import { DesignerActionsContext, type DesignerActions } from './DesignerActionsContext'

const nodeTypes = { workflow: WorkflowNode }
const edgeTypes = { workflow: WorkflowEdge }
const DEFAULT_EDGE_OPTIONS = {
  type: 'workflow',
  markerEnd: { type: MarkerType.ArrowClosed, width: 16, height: 16 },
}
/** palette → canvas 拖放的自訂 MIME；瀏覽器 DnD 只在同一次拖曳中傳遞，不落地。 */
const NODE_DND_MIME = 'application/x-workflow-node-type'
/** 模組層常數：identity 穩定，避免 React Flow 每次 render 重綁鍵盤監聽。 */
const DELETE_KEYS = ['Backspace', 'Delete']
/** n8n 慣例：左鍵框選、中鍵平移（1 = 滑鼠中鍵），右鍵留給情境選單。 */
const PAN_BUTTONS = [1]
const NUDGE_STEP = 8
const NUDGE_STEP_LARGE = 40
/** 連續方向鍵微調在此時間窗內合併成一筆歷史。 */
const NUDGE_MERGE_MS = 600
/** 相容性試算用的暫時節點 id（只存在於 canConnect 的探測圖中，不會進入草稿）。 */
const PROBE_ID = '__workflow-picker-probe__'
/** catalog `kind` 顯示字；未知值原樣顯示。 */
const KIND_LABEL: Record<string, string> = { control: '流程控制', data: '資料' }

const SHORTCUTS: [string, string][] = [
  ['Ctrl/⌘ + Z', '復原'],
  ['Ctrl/⌘ + Shift + Z、Ctrl + Y', '重做'],
  ['Ctrl/⌘ + C / X / V / D', '複製 / 剪下 / 貼上 / 再製'],
  ['Ctrl/⌘ + A', '全選'],
  ['Delete / Backspace', '刪除（必要節點不可刪）'],
  ['Tab', '開啟節點選擇器（焦點在畫布時；Shift + Tab 一律離開畫布）'],
  ['Ctrl/⌘ + S', '儲存草稿'],
  ['Shift + Alt + T', '自動排版'],
  ['方向鍵 / Shift + 方向鍵', '移動選取節點 8px / 40px'],
  ['滑鼠左鍵拖曳空白處', '框選'],
  ['滑鼠中鍵拖曳、空白鍵 + 拖曳', '平移畫布'],
  ['滾輪 / Ctrl + 滾輪', '平移 / 縮放'],
  ['?', '開啟本說明'],
  ['Esc', '關閉浮層或取消選取'],
]

type CanvasNode = Node<CanvasNodeData>
type Point = { x: number; y: number }

type PickerState =
  | { mode: 'add'; x: number; y: number; flow: Point }
  | { mode: 'connect'; x: number; y: number; flow: Point; source: { nodeId: string; port: string } }
  | { mode: 'insert'; x: number; y: number; flow: Point; edgeId: string }

type MenuState = { kind: 'node' | 'selection' | 'edge' | 'pane'; x: number; y: number; flow: Point; id?: string }

interface WorkflowDesignerProps {
  definition: WorkflowDefinition
  uiMetadata: WorkflowUiMetadata
  catalog: WorkflowNodeType[]
  validation: WorkflowValidation | null
  simulation: WorkflowSimulation | null
  /** Runtime trace is display-only and is never written back into Graph IR. */
  runtimeTrace?: WorkflowTraceEntry[]
  disabled: boolean
  canUndo?: boolean
  canRedo?: boolean
  onUndo?: () => void
  onRedo?: () => void
  onSave?: () => void
  /** `coalesce`：與上一筆合併成同一筆歷史（連續方向鍵微調）。 */
  onChange: (definition: WorkflowDefinition, metadata: WorkflowUiMetadata, coalesce?: boolean) => void
}

/** screenToFlowPosition(拖放落點)需要 provider context，故實作拆進 inner component。 */
export default function WorkflowDesigner(props: WorkflowDesignerProps) {
  return <ReactFlowProvider><Designer {...props} /></ReactFlowProvider>
}

/** 只有這個小元件訂閱 viewport，平移/縮放才不會重繪整個 Designer。 */
function ZoomBadge() {
  const { zoom } = useViewport()
  return <span className="workflow-designer__zoom">{Math.round(zoom * 100)}%</span>
}

function groupByKind(items: WorkflowNodeType[]): [string, WorkflowNodeType[]][] {
  const byKind = new Map<string, WorkflowNodeType[]>()
  for (const item of items) {
    const bucket = byKind.get(item.kind)
    if (bucket) bucket.push(item)
    else byKind.set(item.kind, [item])
  }
  return [...byKind.entries()]
}

function Designer({
  definition, uiMetadata, catalog, validation, simulation, runtimeTrace, disabled,
  canUndo, canRedo, onUndo, onRedo, onSave, onChange,
}: WorkflowDesignerProps) {
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [configJsonError, setConfigJsonError] = useState<string | null>(null)
  const [picker, setPicker] = useState<PickerState | null>(null)
  const [menu, setMenu] = useState<MenuState | null>(null)
  const [helpOpen, setHelpOpen] = useState(false)
  const [paletteQuery, setPaletteQuery] = useState('')
  const { screenToFlowPosition, getNodes, fitView } = useReactFlow()
  const invalid = useMemo(() => new Set((validation?.errors ?? []).flatMap((e) => e.id ? [e.id] : [])), [validation])
  const trace = useMemo(() => new Map([...(simulation?.trace ?? []), ...(runtimeTrace ?? [])].map((e) => [e.node_id, e.status])), [runtimeTrace, simulation])
  const canvas = useMemo(() => toCanvas(definition, uiMetadata, catalog, invalid, trace), [definition, uiMetadata, catalog, invalid, trace])
  // React Flow 狀態永遠只是投影：本地持有選取/拖曳中間態，語意仍以 props 為唯一真相。
  const [nodes, setNodes] = useNodesState<CanvasNode>(canvas.nodes)
  const [edges, setEdges] = useEdgesState<Edge>(canvas.edges)
  const pendingSelect = useRef<string | string[] | null>(null)
  /** 快捷鍵的最外層範圍：編輯器之外的鍵盤事件一律不接手。 */
  const rootRef = useRef<HTMLDivElement>(null)
  const canvasRef = useRef<HTMLDivElement>(null)
  const inspectorRef = useRef<HTMLElement>(null)
  const helpCloseRef = useRef<HTMLButtonElement>(null)
  /** 最後一次滑鼠在畫布上的螢幕座標，供貼上/Tab 定位。 */
  const pointer = useRef<Point | null>(null)
  const nudgeAt = useRef(0)
  const selected = definition.nodes.find((node) => node.id === selectedId) ?? null
  const selectedType = selected && catalog.find((item) => item.type === selected.type && item.version === selected.typeVersion)
  const selectedDeletable = !!selected && canDeleteNode(catalog, definition.kind, selected.type, selected.typeVersion, definition.runtimeVariant)
  // 形狀不足以枚舉(巢狀/未知關鍵字)= null,inspector 退回原始 JSON textarea 逃生口。
  const configFields = useMemo(() => selectedType ? parseConfigSchema(selectedType.configSchema) : null, [selectedType])
  const palette = useMemo(
    () => catalogForKind(catalog, definition.kind, definition.runtimeVariant),
    [catalog, definition.kind, definition.runtimeVariant],
  )

  useEffect(() => {
    setNodes((prev) => syncCanvasNodes(canvas.nodes, prev, pendingSelect.current))
    setEdges((prev) => syncCanvasEdges(canvas.edges, prev))
    pendingSelect.current = null
  }, [canvas, setEdges, setNodes])

  // React Flow 的 deleteElements 在同一個 tick 先送 edge remove、再送 node remove，
  // 此時 props 還沒回流；用 ref 承接最新一次提交，第二次刪除才不會把第一次的結果蓋回去
  // （選取「一個節點 + 一條無關的邊」一起刪時最明顯）。render 一律以 props 覆寫。
  const latest = useRef({ definition, uiMetadata })
  latest.current = { definition, uiMetadata }

  /** 唯一的語意提交出口：同 tick 連續提交靠 latest 接力，歷史由呼叫端以 coalesce 控制。
   * onChange 走 ref：呼叫端傳 inline arrow 也不會讓 commit → actions → context value 每次
   * render 換 identity（那會讓 WorkflowNode/WorkflowEdge 的 memo 完全失效）。 */
  const onChangeRef = useRef(onChange)
  onChangeRef.current = onChange
  const commit = useCallback((next: WorkflowDefinition, metadata: WorkflowUiMetadata, coalesce = false) => {
    latest.current = { definition: next, uiMetadata: metadata }
    onChangeRef.current(next, metadata, coalesce)
  }, [])

  /** 刪除的唯一提交路徑（鍵盤、inspector、hover 工具列、右鍵選單共用）：canDeleteNode fail-closed，
   * 被擋下的節點連本地投影都不移除，不會閃爍消失又出現。回傳實際被移除的節點 id。
   *
   * 保護只涵蓋節點，不涵蓋連線：Ctrl+A + Delete 後留下孤立的必要階段是刻意的 —— React Flow 的
   * 邊刪除比節點刪除早一個 change 批次送達，此處無從分辨「連帶刪除」與使用者明確刪這條線
   * （右鍵「刪除連線」是合法操作，不可一起擋）。孤立的必要階段仍是可見結果、可 Ctrl+Z 復原，
   * 且伺服器 validator 會以 unreachable_node / dead_end / governance_stage_order 擋下驗證與發布。 */
  const commitRemoval = useCallback((nodeIds: string[], edgeIds: string[]): Set<string> => {
    const removed = new Set<string>()
    if (disabled) return removed
    const current = latest.current.definition
    const next = removeSemanticNodes(removeSemanticEdges(current, edgeIds), nodeIds, catalog)
    for (const id of nodeIds) if (!next.nodes.some((node) => node.id === id)) removed.add(id)
    if (next.nodes.length === current.nodes.length && next.edges.length === current.edges.length) return removed
    const metadata = latest.current.uiMetadata
    const positions = Object.fromEntries(Object.entries(metadata.positions).filter(([id]) => !removed.has(id)))
    commit(next, { ...metadata, positions })
    return removed
  }, [catalog, commit, disabled])

  const commitPositions = useCallback((moved: { id: string; position: Point }[], coalesce: boolean) => {
    if (disabled || moved.length === 0) return
    commit(latest.current.definition, patchPositions(latest.current.uiMetadata, moved), coalesce)
  }, [commit, disabled])

  const onNodesChange = useCallback((changes: NodeChange<CanvasNode>[]) => {
    const removed = commitRemoval(changes.flatMap((change) => change.type === 'remove' ? [change.id] : []), [])
    setNodes((prev) => applyNodeChanges(changes.filter((change) => change.type !== 'remove' || removed.has(change.id)), prev))
    // 位置提交的唯一出口：拖曳結束與程式化移動都會送出 dragging !== true 的 position change。
    const moved = changes.flatMap((change) =>
      change.type === 'position' && change.dragging !== true && change.position
        ? [{ id: change.id, position: change.position }] : [])
    commitPositions(moved, false)
  }, [commitPositions, commitRemoval, setNodes])

  const onEdgesChange = useCallback((changes: EdgeChange<Edge>[]) => {
    commitRemoval([], changes.flatMap((change) => change.type === 'remove' ? [change.id] : []))
    setEdges((prev) => applyEdgeChanges(disabled ? changes.filter((change) => change.type !== 'remove') : changes, prev))
  }, [commitRemoval, disabled, setEdges])

  // Inspector 跟隨畫布選取（單選才有可編輯對象），選取本身是 UI-only、不進語意定義。
  const onSelectionChange = useCallback(
    ({ nodes: picked }: { nodes: Node[] }) => setSelectedId(picked.length === 1 ? picked[0].id : null),
    [],
  )

  const onConnect: OnConnect = useCallback((connection: Connection) => {
    const current = latest.current.definition
    if (disabled || !canConnect(connection, current, catalog)) return
    if (!connection.source || !connection.target || !connection.sourceHandle || !connection.targetHandle) return
    commit({ ...current, edges: [...current.edges, {
      id: crypto.randomUUID(), source: { nodeId: connection.source, port: connection.sourceHandle }, target: { nodeId: connection.target, port: connection.targetHandle },
    }] }, latest.current.uiMetadata)
  }, [catalog, commit, disabled])

  const addNode = useCallback((type: WorkflowNodeType, position?: Point) => {
    if (disabled) return
    const { definition: current, uiMetadata: metadata } = latest.current
    const id = newNodeId()
    const at = position ?? { x: 80 + current.nodes.length * 24, y: 80 + current.nodes.length * 24 }
    commit({ ...current, nodes: [...current.nodes, { id, type: type.type, typeVersion: type.version, config: {} }] }, {
      ...metadata, positions: { ...metadata.positions, [id]: at },
    })
    pendingSelect.current = id
    setSelectedId(id)
  }, [commit, disabled])

  const canvasCenter = useCallback((): Point => {
    const rect = canvasRef.current?.getBoundingClientRect()
    return rect ? { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 } : { x: 0, y: 0 }
  }, [])

  const openAddPicker = useCallback((at?: Point) => {
    if (disabled) return
    const point = at ?? pointer.current ?? canvasCenter()
    setPicker({ mode: 'add', x: point.x, y: point.y, flow: screenToFlowPosition(point) })
  }, [canvasCenter, disabled, screenToFlowPosition])

  const selectedNodeIds = useCallback(() => getNodes().filter((node) => node.selected).map((node) => node.id), [getNodes])

  const copy = useCallback((ids?: string[]) => {
    const payload = copySelection(latest.current.definition, latest.current.uiMetadata, ids ?? selectedNodeIds())
    if (payload) writeClipboard(payload)
  }, [selectedNodeIds])

  const paste = useCallback((at?: Point | null) => {
    if (disabled) return
    const payload = readClipboard()
    if (!payload) return
    const target = at ?? (pointer.current ? screenToFlowPosition(pointer.current) : null)
    const result = pasteClipboard(latest.current.definition, latest.current.uiMetadata, payload, catalog, target)
    if (!result) return
    commit(result.definition, result.ui_metadata)
    pendingSelect.current = result.ids
  }, [catalog, commit, disabled, screenToFlowPosition])

  const duplicate = useCallback((ids: string[]) => {
    if (disabled) return
    const { definition: current, uiMetadata: metadata } = latest.current
    const payload = copySelection(current, metadata, ids)
    if (!payload) return
    const result = pasteClipboard(current, metadata, payload, catalog, null)
    if (!result) return
    commit(result.definition, result.ui_metadata)
    pendingSelect.current = result.ids
  }, [catalog, commit, disabled])

  const cut = useCallback(() => {
    if (disabled) return
    // 不可刪的節點留在畫布上，也不進剪貼簿（剪下不得變成隱形複製）。
    const ids = getNodes()
      .filter((node) => node.selected && (node.data as CanvasNodeData).deletable)
      .map((node) => node.id)
    if (ids.length === 0) return
    copy(ids)
    commitRemoval(ids, [])
  }, [commitRemoval, copy, disabled, getNodes])

  const nudge = useCallback((dx: number, dy: number) => {
    if (disabled) return
    const moved = getNodes().filter((node) => node.selected)
      .map((node) => ({ id: node.id, position: { x: node.position.x + dx, y: node.position.y + dy } }))
    if (moved.length === 0) return
    setNodes((prev) => applyNodeChanges(
      moved.map((item) => ({ type: 'position' as const, id: item.id, position: item.position })), prev,
    ))
    const now = Date.now()
    commitPositions(moved, now - nudgeAt.current < NUDGE_MERGE_MS)
    nudgeAt.current = now
  }, [commitPositions, disabled, getNodes, setNodes])

  const selectAll = useCallback(() => setNodes((prev) => prev.map((node) => ({ ...node, selected: true }))), [setNodes])
  const clearSelection = useCallback(() => {
    setNodes((prev) => prev.map((node) => ({ ...node, selected: false })))
    setEdges((prev) => prev.map((edge) => ({ ...edge, selected: false })))
  }, [setEdges, setNodes])

  const runLayout = useCallback(async () => {
    if (disabled) return
    commit(latest.current.definition, await autoLayout(latest.current.definition, latest.current.uiMetadata))
  }, [commit, disabled])

  const focusInspector = useCallback((id: string) => {
    setNodes((prev) => prev.map((node) => ({ ...node, selected: node.id === id })))
    setSelectedId(id)
    requestAnimationFrame(() => inspectorRef.current?.focus())
  }, [setNodes])

  const onConnectEnd: OnConnectEnd = useCallback((event, state) => {
    if (disabled || state.isValid || !state.fromNode || !state.fromHandle?.id) return
    if (state.fromHandle.type !== 'source') return
    // 只有真的落在畫布空白處才開 picker（落在無效 handle 上不算）。
    if (!(event.target as Element | null)?.closest?.('.react-flow__pane')) return
    const point = 'clientX' in event
      ? { x: event.clientX, y: event.clientY }
      : { x: event.changedTouches[0].clientX, y: event.changedTouches[0].clientY }
    setPicker({
      mode: 'connect', x: point.x, y: point.y, flow: screenToFlowPosition(point),
      source: { nodeId: state.fromNode.id, port: state.fromHandle.id },
    })
  }, [disabled, screenToFlowPosition])

  /** picker 一律先過 catalogForKind，再依入口加相容性過濾（判準仍是 canConnect）。 */
  const pickerItems = useMemo(() => {
    if (!picker) return []
    if (picker.mode === 'connect') {
      return palette.filter((type) => addConnectedNode(definition, picker.source, type, catalog, PROBE_ID).connected)
    }
    if (picker.mode === 'insert') {
      return palette.filter((type) => insertNodeOnEdge(definition, picker.edgeId, type, catalog, PROBE_ID) !== null)
    }
    return palette
  }, [catalog, definition, palette, picker])

  const pickNode = useCallback((type: WorkflowNodeType) => {
    const current = picker
    setPicker(null)
    if (!current || disabled) return
    if (current.mode === 'add') { addNode(type, current.flow); return }
    const { definition: draft, uiMetadata: metadata } = latest.current
    const id = newNodeId()
    const next = current.mode === 'connect'
      ? addConnectedNode(draft, current.source, type, catalog, id).definition
      : insertNodeOnEdge(draft, current.edgeId, type, catalog, id)
    if (!next) return
    commit(next, { ...metadata, positions: { ...metadata.positions, [id]: current.flow } })
    pendingSelect.current = id
    setSelectedId(id)
  }, [addNode, catalog, commit, disabled, picker])

  const actions = useMemo<DesignerActions>(() => ({
    disabled,
    focusInspector,
    duplicateNode: (id) => duplicate([id]),
    deleteNode: (id) => { if (commitRemoval([id], []).size) setSelectedId(null) },
    deleteEdge: (id) => { commitRemoval([], [id]) },
    insertNodeOnEdge: (edgeId, at) => {
      if (disabled) return
      setPicker({ mode: 'insert', x: at.x, y: at.y, flow: screenToFlowPosition(at), edgeId })
    },
  }), [commitRemoval, disabled, duplicate, focusInspector, screenToFlowPosition])

  const openMenu = useCallback((event: ReactMouseEvent | MouseEvent, state: Omit<MenuState, 'x' | 'y' | 'flow'>) => {
    event.preventDefault()
    const point = { x: event.clientX, y: event.clientY }
    setMenu({ ...state, x: point.x, y: point.y, flow: screenToFlowPosition(point) })
  }, [screenToFlowPosition])

  const menuItems = useCallback((state: MenuState): ContextMenuItem[] => {
    const locked = disabled
    if (state.kind === 'node' && state.id) {
      const id = state.id
      const data = nodes.find((node) => node.id === id)?.data as CanvasNodeData | undefined
      const deletable = !!data?.deletable
      // 必要階段每種型別只能有一個：再製出第二個必定被 validator 擋下，入口直接封住。
      const duplicable = !data?.required
      return [
        { label: '編輯設定', onSelect: () => focusInspector(id) },
        {
          label: '再製', disabled: locked || !duplicable,
          title: duplicable ? undefined : '必要節點不可再製',
          onSelect: () => duplicate([id]),
        },
        { label: '複製', onSelect: () => copy([id]) },
        {
          label: '刪除', disabled: locked || !deletable,
          title: deletable ? undefined : '必要節點不可刪除',
          onSelect: () => { if (commitRemoval([id], []).size) setSelectedId(null) },
        },
      ]
    }
    if (state.kind === 'selection') {
      const ids = selectedNodeIds()
      return [
        { label: '複製', onSelect: () => copy(ids) },
        { label: '再製', disabled: locked, onSelect: () => duplicate(ids) },
        { label: '刪除', disabled: locked, title: '僅刪除可刪除的節點', onSelect: () => commitRemoval(ids, []) },
      ]
    }
    if (state.kind === 'edge' && state.id) {
      const id = state.id
      return [
        { label: '中插節點', disabled: locked, onSelect: () => actions.insertNodeOnEdge(id, { x: state.x, y: state.y }) },
        { label: '刪除連線', disabled: locked, onSelect: () => commitRemoval([], [id]) },
      ]
    }
    return [
      { label: '新增節點', disabled: locked, onSelect: () => openAddPicker({ x: state.x, y: state.y }) },
      { label: '貼上', disabled: locked || !readClipboard(), onSelect: () => paste(state.flow) },
      { label: '全選', onSelect: selectAll },
      { label: '自動排版', disabled: locked, onSelect: () => void runLayout() },
      { label: '適應畫面', onSelect: () => void fitView() },
    ]
  }, [actions, commitRemoval, copy, disabled, duplicate, fitView, focusInspector, nodes, openAddPicker, paste, runLayout, selectAll, selectedNodeIds])

  // 快捷鍵：capture 階段攔截，才能在 React Flow 內建的節點鍵盤處理之前接手（方向鍵步幅要 8/40px）。
  // 範圍守衛（take() 一律 preventDefault + stopPropagation，超出範圍就是吃掉整頁的原生鍵）：
  // 焦點不在編輯器內完全不接手；畫布操作鍵再收斂到畫布內，只有 undo/redo/儲存/排版放寬到整個
  // 編輯器（用 palette 按鈕新增節點後焦點停在 palette，Ctrl+Z 仍要能復原）。
  useEffect(() => {
    const handler = (event: KeyboardEvent) => {
      const target = event.target as globalThis.Node | null
      if (!target || !rootRef.current?.contains(target)) return
      const inCanvas = !!canvasRef.current?.contains(target)
      // 焦點守衛 + 浮層自理鍵盤：兩者都不得讓畫布快捷鍵誤觸。
      if (isTextEntryTarget(event.target) || picker || menu || helpOpen) return
      const take = (run: () => void) => { event.preventDefault(); event.stopPropagation(); run() }
      const edit = (run: () => void) => take(() => { if (!disabled) run() })
      const mod = event.ctrlKey || event.metaKey
      if (!mod) {
        if (event.shiftKey && event.altKey && event.code === 'KeyT') return edit(() => void runLayout())
        if (!inCanvas) return
        if (event.key === '?') return take(() => setHelpOpen(true))
        if (event.key === 'Escape') return take(clearSelection)
        // Tab 只在焦點真的落在畫布容器本身時才變成「開節點選擇器」，Shift+Tab 一律放行：
        // undo/redo/自動排版/? 與 React Flow <Controls> 都在畫布 div 內，攔多了焦點就再也離不開
        // 畫布（WCAG 2.1.2 鍵盤陷阱）。
        if (event.key === 'Tab' && !event.shiftKey && event.target === canvasRef.current) {
          return edit(() => openAddPicker())
        }
        if (event.key.startsWith('Arrow')) {
          const step = event.shiftKey ? NUDGE_STEP_LARGE : NUDGE_STEP
          const delta: Record<string, [number, number]> = {
            ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step],
          }
          const move = delta[event.key]
          // 沒有選取節點時不攔截：方向鍵仍要能捲動頁面（畫布下方還有驗證結果與版本歷史）。
          if (move && !disabled && selectedNodeIds().length > 0) return take(() => nudge(move[0], move[1]))
        }
        return
      }
      const key = event.key.toLowerCase()
      if (key === 'z') return edit(() => { if (event.shiftKey) onRedo?.(); else onUndo?.() })
      if (key === 'y') return edit(() => onRedo?.())
      if (key === 's') return edit(() => onSave?.())
      if (!inCanvas) return
      if (key === 'c') {
        // 沒有選取節點、或使用者正選著編輯器內的文字（inspector 的節點 id）時放行原生複製，
        // 否則按下 Ctrl+C 是兩邊都沒複製到東西。
        const selection = window.getSelection()
        const copyingText = !!selection && !selection.isCollapsed
          && !!selection.anchorNode && !!rootRef.current?.contains(selection.anchorNode)
        if (copyingText || selectedNodeIds().length === 0) return
        return take(() => copy())
      }
      if (key === 'x') return edit(cut)
      if (key === 'v') return edit(() => paste())
      if (key === 'd') return edit(() => duplicate(selectedNodeIds()))
      if (key === 'a') return take(selectAll)
    }
    window.addEventListener('keydown', handler, true)
    return () => window.removeEventListener('keydown', handler, true)
  }, [
    clearSelection, copy, cut, disabled, duplicate, helpOpen, menu, nudge, onRedo, onSave, onUndo,
    openAddPicker, paste, picker, runLayout, selectAll, selectedNodeIds,
  ])

  function onDrop(event: DragEvent<HTMLDivElement>) {
    if (disabled) return
    const payload = event.dataTransfer.getData(NODE_DND_MIME)
    if (!payload) return
    event.preventDefault()
    const at = payload.lastIndexOf('@')
    // 只認 palette 目前允許的節點型別（catalogForKind fail-closed），不信任 payload。
    const type = palette.find((item) => item.type === payload.slice(0, at) && item.version === payload.slice(at + 1))
    if (type) addNode(type, screenToFlowPosition({ x: event.clientX, y: event.clientY }))
  }

  function writeConfig(config: Record<string, unknown>) {
    if (!selected) return
    commit({ ...definition, nodes: definition.nodes.map((node) => node.id === selected.id ? { ...node, config } : node) }, uiMetadata)
  }

  function patchConfig(text: string) {
    if (!selected) return
    try {
      writeConfig(JSON.parse(text) as Record<string, unknown>)
      setConfigJsonError(null)
    } catch {
      // Invalid JSON stays local to the textarea until it becomes valid — but that must be
      // visible, not silent, or the author cannot tell "not applied yet" from "applied".
      setConfigJsonError('JSON 格式錯誤，尚未套用；其餘欄位維持前次已生效的值。')
    }
  }

  function patchConfigField(key: string, value: string | number | boolean | undefined) {
    if (!selected) return
    const nextConfig = { ...selected.config }
    if (value === undefined) delete nextConfig[key]
    else nextConfig[key] = value
    writeConfig(nextConfig)
  }

  function configFieldInput(field: WorkflowNodeConfigField) {
    const id = `workflow-node-config-${field.key}`
    const raw = selected?.config[field.key]
    if (field.type === 'boolean') {
      return <input id={id} type="checkbox" disabled={disabled} checked={!!raw}
        onChange={(event) => patchConfigField(field.key, event.target.checked)} />
    }
    if (field.type === 'integer' || field.type === 'number') {
      return <input id={id} className="input" type="number" disabled={disabled}
        min={field.minimum} max={field.maximum} step={field.type === 'integer' ? 1 : 'any'}
        value={raw === undefined ? '' : String(raw)}
        onChange={(event) => {
          const text = event.target.value
          if (text === '') { patchConfigField(field.key, undefined); return }
          const n = Number(text)
          if (Number.isFinite(n)) patchConfigField(field.key, n)
        }} />
    }
    return <input id={id} className="input" type="text" disabled={disabled}
      value={raw === undefined ? '' : String(raw)}
      onChange={(event) => patchConfigField(field.key, event.target.value === '' ? undefined : event.target.value)} />
  }

  useEffect(() => { if (selectedId && !selected) setSelectedId(null) }, [selected, selectedId])
  useEffect(() => { setConfigJsonError(null) }, [selectedId])

  const paletteGroups = useMemo(() => {
    const needle = paletteQuery.trim().toLowerCase()
    return groupByKind(palette.filter((type) => !needle
      || type.title.toLowerCase().includes(needle) || type.type.toLowerCase().includes(needle)))
  }, [palette, paletteQuery])

  return (
    <DesignerActionsContext.Provider value={actions}>
      <div className="workflow-designer" ref={rootRef}>
        <aside className="workflow-designer__palette">
          <h4>節點目錄</h4>
          <input className="input" type="search" aria-label="搜尋節點目錄" placeholder="搜尋節點"
            value={paletteQuery} onChange={(event) => setPaletteQuery(event.target.value)} />
          <button type="button" className="btn" disabled={disabled} onClick={() => openAddPicker(canvasCenter())}>
            ＋ 新增節點
          </button>
          {paletteGroups.length === 0 && <p className="muted">沒有符合的節點型別。</p>}
          {paletteGroups.map(([kind, items]) => (
            <details className="workflow-designer__group" key={kind} open>
              <summary>{KIND_LABEL[kind] ?? kind}</summary>
              {items.map((type) => (
                <button type="button" className="btn" key={`${type.type}@${type.version}`} disabled={disabled}
                  draggable={!disabled}
                  title={`${type.type}@${type.version}`}
                  onDragStart={(event) => {
                    event.dataTransfer.setData(NODE_DND_MIME, `${type.type}@${type.version}`)
                    event.dataTransfer.effectAllowed = 'move'
                  }}
                  onClick={() => addNode(type)}>
                  ＋ {type.title}
                </button>
              ))}
            </details>
          ))}
        </aside>
        {/* tabIndex -1：點畫布後焦點落在這裡，只有此時 Tab 才是「開節點選擇器」；它不進 Tab 順序，
            畫布內的節點與按鈕（undo/redo/自動排版/?/Controls）維持原生 Tab／Shift+Tab 巡覽。 */}
        <div className="workflow-designer__canvas" ref={canvasRef} tabIndex={-1} onDrop={onDrop}
          onMouseMove={(event) => { pointer.current = { x: event.clientX, y: event.clientY } }}
          onDragOver={(event) => { if (disabled) return; event.preventDefault(); event.dataTransfer.dropEffect = 'move' }}>
          <ReactFlow nodes={nodes} edges={edges} nodeTypes={nodeTypes} edgeTypes={edgeTypes}
            defaultEdgeOptions={DEFAULT_EDGE_OPTIONS}
            onNodesChange={onNodesChange} onEdgesChange={onEdgesChange}
            onConnect={onConnect} onConnectEnd={onConnectEnd}
            isValidConnection={(connection) => canConnect(connection, latest.current.definition, catalog)}
            onReconnect={(oldEdge, connection) => { if (!disabled) commit(reconnectSemanticEdge(latest.current.definition, oldEdge.id, connection, catalog), latest.current.uiMetadata) }}
            onSelectionChange={onSelectionChange}
            onPaneClick={() => canvasRef.current?.focus()}
            onMoveEnd={(_, viewport) => { if (!disabled) commit(latest.current.definition, patchViewport(latest.current.uiMetadata, viewport)) }}
            onNodeContextMenu={(event, node) => openMenu(event, { kind: 'node', id: node.id })}
            onSelectionContextMenu={(event) => openMenu(event, { kind: 'selection' })}
            onEdgeContextMenu={(event, edge) => openMenu(event, { kind: 'edge', id: edge.id })}
            onPaneContextMenu={(event) => openMenu(event, { kind: 'pane' })}
            deleteKeyCode={disabled ? null : DELETE_KEYS}
            panOnDrag={PAN_BUTTONS} panActivationKeyCode="Space" selectionOnDrag selectionMode={SelectionMode.Partial}
            panOnScroll zoomOnScroll={false} zoomOnPinch
            {...(uiMetadata.viewport ? { defaultViewport: uiMetadata.viewport } : { fitView: true })}
            nodesDraggable={!disabled} nodesConnectable={!disabled} edgesReconnectable={!disabled} elementsSelectable>
            <Background variant={BackgroundVariant.Dots} gap={18} size={1} />
            <Controls />
            <MiniMap pannable zoomable nodeClassName={(node) => {
              const data = node.data as CanvasNodeData
              return data.invalid ? 'workflow-mini--invalid' : data.required ? 'workflow-mini--required' : 'workflow-mini--normal'
            }} />
            <Panel position="bottom-left" className="workflow-designer__viewbar">
              <ZoomBadge />
              <button type="button" className="btn" onClick={() => void fitView()}>適應畫面</button>
            </Panel>
          </ReactFlow>
          <div className="workflow-designer__toolbar">
            <button type="button" className="btn" disabled={disabled || !canUndo} title="復原（Ctrl/⌘+Z）"
              onClick={() => onUndo?.()}>↩ 復原</button>
            <button type="button" className="btn" disabled={disabled || !canRedo} title="重做（Ctrl/⌘+Shift+Z）"
              onClick={() => onRedo?.()}>↪ 重做</button>
            <button type="button" className="btn workflow-designer__layout" disabled={disabled}
              title="自動排版（Shift+Alt+T）" onClick={() => void runLayout()}>自動排版</button>
            <button type="button" className="btn" title="快捷鍵說明（?）" aria-label="快捷鍵說明"
              onClick={() => setHelpOpen(true)}>?</button>
          </div>
        </div>
        <aside className="workflow-designer__inspector" ref={inspectorRef} tabIndex={-1}>
          <h4>節點屬性</h4>
          {selected && selectedType ? <>
            <p><strong>{selectedType.title}</strong><br /><span className="muted">{selected.id}</span></p>
            {configFields ? (
              configFields.length === 0 ? <p className="muted">此節點無可設定項。</p> : (
                configFields.map((field) => (
                  <div className="field" key={field.key}>
                    <label htmlFor={`workflow-node-config-${field.key}`}>
                      {field.key}{field.required && '（必填）'}
                      {field.minimum !== undefined && <span className="muted"> 最小 {field.minimum}</span>}
                      {field.maximum !== undefined && <span className="muted"> 最大 {field.maximum}</span>}
                    </label>
                    {configFieldInput(field)}
                  </div>
                ))
              )
            ) : <>
              <label htmlFor="workflow-node-config">設定（JSON）</label>
              <textarea id="workflow-node-config" className="input workflow-designer__config" disabled={disabled}
                defaultValue={JSON.stringify(selected.config, null, 2)} key={selected.id} onChange={(event) => patchConfig(event.target.value)} />
              {configJsonError && <p className="field-error" role="alert">{configJsonError}</p>}
            </>}
            <button type="button" className="btn btn--danger workflow-designer__delete"
              disabled={disabled || !selectedDeletable}
              title={selectedDeletable ? undefined : '必要節點不可刪除'}
              onClick={() => { if (commitRemoval([selected.id], []).size) setSelectedId(null) }}
            >刪除節點</button>
          </> : <p className="muted">選取節點以編輯受 catalog schema 約束的設定。</p>}
        </aside>
      </div>
      {picker && (
        <NodePicker
          title={picker.mode === 'add' ? '新增節點' : picker.mode === 'connect' ? '接上新節點' : '中插節點'}
          items={pickerItems} x={picker.x} y={picker.y}
          onPick={pickNode} onClose={() => setPicker(null)}
        />
      )}
      {menu && (
        <ContextMenu
          x={menu.x} y={menu.y} label="畫布操作" items={menuItems(menu)} onClose={() => setMenu(null)}
        />
      )}
      {/* 說明浮層沿用既有 Modal（原生 <dialog>）：focus-trap／Escape／焦點還原都免費拿到。 */}
      <Modal open={helpOpen} onClose={() => setHelpOpen(false)} className="modal-host" initialFocusRef={helpCloseRef}
        aria-label="鍵盤快捷鍵">
        <div className="workflow-help">
          <h4>鍵盤快捷鍵</h4>
          <table className="table">
            <thead><tr><th>鍵</th><th>行為</th></tr></thead>
            <tbody>{SHORTCUTS.map(([keys, description]) => (
              <tr key={keys}><td>{keys}</td><td>{description}</td></tr>
            ))}</tbody>
          </table>
          <button type="button" className="btn" ref={helpCloseRef} onClick={() => setHelpOpen(false)}>關閉</button>
        </div>
      </Modal>
    </DesignerActionsContext.Provider>
  )
}
