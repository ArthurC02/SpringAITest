import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  Background, Controls, MiniMap, ReactFlow, applyNodeChanges,
  type Connection, type EdgeChange, type NodeChange, type OnConnect, type OnEdgesChange, type OnNodesChange,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import type {
  WorkflowDefinition, WorkflowNodeType, WorkflowSimulation, WorkflowUiMetadata, WorkflowValidation,
  WorkflowTraceEntry,
} from '../types'
import { canConnect } from './connection'
import { patchPositions, patchViewport, toCanvas } from './graphAdapter'
import { autoLayout } from './layout'
import WorkflowNode from './WorkflowNode'
import { catalogForKind } from './catalog'
import { reconnectSemanticEdge, removeSemanticEdges, removeSemanticNodes } from './editing'

const nodeTypes = { workflow: WorkflowNode }

export default function WorkflowDesigner({
  definition, uiMetadata, catalog, validation, simulation, runtimeTrace, disabled, onChange,
}: {
  definition: WorkflowDefinition
  uiMetadata: WorkflowUiMetadata
  catalog: WorkflowNodeType[]
  validation: WorkflowValidation | null
  simulation: WorkflowSimulation | null
  /** Runtime trace is display-only and is never written back into Graph IR. */
  runtimeTrace?: WorkflowTraceEntry[]
  disabled: boolean
  onChange: (definition: WorkflowDefinition, metadata: WorkflowUiMetadata) => void
}) {
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const invalid = useMemo(() => new Set((validation?.errors ?? []).flatMap((e) => e.id ? [e.id] : [])), [validation])
  const trace = useMemo(() => new Map([...(simulation?.trace ?? []), ...(runtimeTrace ?? [])].map((e) => [e.node_id, e.status])), [runtimeTrace, simulation])
  const canvas = useMemo(() => toCanvas(definition, uiMetadata, catalog, invalid, trace), [definition, uiMetadata, catalog, invalid, trace])
  const selected = definition.nodes.find((node) => node.id === selectedId) ?? null
  const selectedType = selected && catalog.find((item) => item.type === selected.type && item.version === selected.typeVersion)

  const onNodesChange: OnNodesChange = useCallback((changes: NodeChange[]) => {
    if (disabled) return
    const removedIds = changes.filter((change) => change.type === 'remove').map((change) => change.id)
    const nextDefinition = removeSemanticNodes(definition, removedIds, catalog)
    const kept = applyNodeChanges(changes.filter((change) => change.type !== 'remove'), canvas.nodes)
      .filter((node) => nextDefinition.nodes.some((item) => item.id === node.id))
    const metadata = patchPositions(uiMetadata, kept)
    const positions = Object.fromEntries(Object.entries(metadata.positions).filter(([id]) => nextDefinition.nodes.some((node) => node.id === id)))
    onChange(nextDefinition, { ...metadata, positions })
  }, [canvas.nodes, catalog, definition, disabled, onChange, uiMetadata])

  const onEdgesChange: OnEdgesChange = useCallback((changes: EdgeChange[]) => {
    if (disabled) return
    onChange(removeSemanticEdges(definition, changes.filter((change) => change.type === 'remove').map((change) => change.id)), uiMetadata)
  }, [definition, disabled, onChange, uiMetadata])

  const onConnect: OnConnect = useCallback((connection: Connection) => {
    if (disabled || !canConnect(connection, definition, catalog)) return
    if (!connection.source || !connection.target || !connection.sourceHandle || !connection.targetHandle) return
    onChange({ ...definition, edges: [...definition.edges, {
      id: crypto.randomUUID(), source: { nodeId: connection.source, port: connection.sourceHandle }, target: { nodeId: connection.target, port: connection.targetHandle },
    }] }, uiMetadata)
  }, [catalog, definition, disabled, onChange, uiMetadata])

  function addNode(type: WorkflowNodeType) {
    if (disabled) return
    const id = `node-${crypto.randomUUID()}`
    onChange({ ...definition, nodes: [...definition.nodes, { id, type: type.type, typeVersion: type.version, config: {} }] }, {
      ...uiMetadata, positions: { ...uiMetadata.positions, [id]: { x: 80 + definition.nodes.length * 24, y: 80 + definition.nodes.length * 24 } },
    })
    setSelectedId(id)
  }

  function patchConfig(text: string) {
    if (!selected) return
    try {
      const config = JSON.parse(text) as Record<string, unknown>
      onChange({ ...definition, nodes: definition.nodes.map((node) => node.id === selected.id ? { ...node, config } : node) }, uiMetadata)
    } catch { /* Keep invalid JSON local to the textarea until it becomes valid. */ }
  }

  async function onLayout() { if (!disabled) onChange(definition, await autoLayout(definition, uiMetadata)) }

  useEffect(() => { if (selectedId && !selected) setSelectedId(null) }, [selected, selectedId])

  return (
    <div className="workflow-designer">
      <aside className="workflow-designer__palette">
        <h4>Node Catalog</h4>
        {catalogForKind(catalog, definition.kind, definition.runtimeVariant).map((type) => (
          <button type="button" className="btn" key={`${type.type}@${type.version}`} disabled={disabled} onClick={() => addNode(type)}>
            ＋ {type.title}
          </button>
        ))}
      </aside>
      <div className="workflow-designer__canvas">
        <ReactFlow nodes={canvas.nodes} edges={canvas.edges} nodeTypes={nodeTypes} onNodesChange={onNodesChange} onEdgesChange={onEdgesChange}
          onConnect={onConnect} isValidConnection={(connection) => canConnect(connection, definition, catalog)}
          onReconnect={(oldEdge, connection) => { if (!disabled) onChange(reconnectSemanticEdge(definition, oldEdge.id, connection, catalog), uiMetadata) }}
          onNodeClick={(_, node) => setSelectedId(node.id)} onMoveEnd={(_, viewport) => onChange(definition, patchViewport(uiMetadata, viewport))}
          fitView nodesDraggable={!disabled} nodesConnectable={!disabled} edgesReconnectable={!disabled} elementsSelectable>
          <Background /><Controls /><MiniMap />
        </ReactFlow>
        <button type="button" className="btn workflow-designer__layout" disabled={disabled} onClick={() => void onLayout()}>自動排版</button>
      </div>
      <aside className="workflow-designer__inspector">
        <h4>Properties</h4>
        {selected && selectedType ? <>
          <p><strong>{selectedType.title}</strong><br /><span className="muted">{selected.id}</span></p>
          <label htmlFor="workflow-node-config">Config (JSON)</label>
          <textarea id="workflow-node-config" className="input workflow-designer__config" disabled={disabled}
            defaultValue={JSON.stringify(selected.config, null, 2)} key={selected.id} onChange={(event) => patchConfig(event.target.value)} />
        </> : <p className="muted">選取節點以編輯受 catalog schema 約束的設定。</p>}
      </aside>
    </div>
  )
}
