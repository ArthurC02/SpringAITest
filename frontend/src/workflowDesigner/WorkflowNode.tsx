import { memo, useState } from 'react'
import { Handle, NodeToolbar, Position, type NodeProps } from '@xyflow/react'
import type { CanvasNodeData } from './graphAdapter'
import { useDesignerActions } from './DesignerActionsContext'

// 模擬/實際執行的節點追蹤狀態顯示字；伺服器可能回傳未列舉的值，原樣顯示。
const TRACE_STATUS_LABEL: Record<string, string> = {
  pending: '等待中',
  running: '執行中',
  passed: '通過',
  failed: '失敗',
  skipped: '已略過',
}

/** 圖示方塊：無 icon 套件，用文字符號自製；未知 kind 退回通用符號。 */
const KIND_GLYPH: Record<string, string> = { control: '⛭', data: '▤' }

const PORT_TOP = 34
const PORT_GAP = 18

function WorkflowNodeCard({ id, data, selected }: NodeProps) {
  const node = data as unknown as CanvasNodeData
  const actions = useDesignerActions()
  const [hovered, setHovered] = useState(false)

  return (
    <div
      className={`workflow-node${node.invalid ? ' workflow-node--invalid' : ''}`}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
    >
      <NodeToolbar isVisible={!actions.disabled && (hovered || !!selected)} position={Position.Top}>
        <div className="workflow-node__tools">
          <button type="button" className="btn" onClick={() => actions.focusInspector(id)}>設定</button>
          <button
            type="button" className="btn" disabled={node.required}
            title={node.required ? '必要節點不可再製' : undefined}
            onClick={() => actions.duplicateNode(id)}
          >再製</button>
          <button
            type="button" className="btn btn--danger" disabled={!node.deletable}
            title={node.deletable ? undefined : '必要節點不可刪除'}
            onClick={() => actions.deleteNode(id)}
          >刪除</button>
        </div>
      </NodeToolbar>
      {node.inputs.map((port, index) => (
        <Handle type="target" position={Position.Left} id={port} key={port} title={port}
          style={{ top: PORT_TOP + index * PORT_GAP }} />
      ))}
      <div className="workflow-node__head">
        <span className="workflow-node__icon" aria-hidden="true">{KIND_GLYPH[node.nodeKind] ?? '◆'}</span>
        <span className="workflow-node__title">
          <strong>{node.title}</strong>
          <span className="workflow-node__type">{node.typeLabel}</span>
        </span>
      </div>
      <div className="workflow-node__badges">
        {node.required && <span className="workflow-node__required">必要</span>}
        {node.invalid && <span className="workflow-node__invalid-badge">⚠ 無效</span>}
        {node.traceStatus && (
          <span className="workflow-node__trace">{TRACE_STATUS_LABEL[node.traceStatus] ?? node.traceStatus}</span>
        )}
      </div>
      {node.outputs.map((port, index) => (
        <Handle type="source" position={Position.Right} id={port} key={port} title={port}
          style={{ top: PORT_TOP + index * PORT_GAP }} />
      ))}
    </div>
  )
}

export default memo(WorkflowNodeCard)
