import { Handle, Position, type NodeProps } from '@xyflow/react'
import type { CanvasNodeData } from './graphAdapter'

// 模擬/實際執行的節點追蹤狀態顯示字；伺服器可能回傳未列舉的值，原樣顯示。
const TRACE_STATUS_LABEL: Record<string, string> = {
  pending: '等待中',
  running: '執行中',
  passed: '通過',
  failed: '失敗',
  skipped: '已略過',
}

export default function WorkflowNode({ data }: NodeProps) {
  const node = data as unknown as CanvasNodeData
  return (
    <div className={`workflow-node${node.invalid ? ' workflow-node--invalid' : ''}`}>
      {node.inputs.map((port, index) => <Handle type="target" position={Position.Left} id={port} key={port} style={{ top: 24 + index * 18 }} />)}
      <strong>{node.title}</strong>
      {node.required && <span className="workflow-node__required">必要</span>}
      {node.traceStatus && (
        <span className="workflow-node__trace">{TRACE_STATUS_LABEL[node.traceStatus] ?? node.traceStatus}</span>
      )}
      {node.outputs.map((port, index) => <Handle type="source" position={Position.Right} id={port} key={port} style={{ top: 24 + index * 18 }} />)}
    </div>
  )
}
