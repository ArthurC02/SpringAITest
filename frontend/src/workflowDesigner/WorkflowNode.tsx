import { Handle, Position, type NodeProps } from '@xyflow/react'
import type { CanvasNodeData } from './graphAdapter'

export default function WorkflowNode({ data }: NodeProps) {
  const node = data as unknown as CanvasNodeData
  return (
    <div className={`workflow-node${node.invalid ? ' workflow-node--invalid' : ''}`}>
      {node.inputs.map((port, index) => <Handle type="target" position={Position.Left} id={port} key={port} style={{ top: 24 + index * 18 }} />)}
      <strong>{node.title}</strong>
      {node.required && <span className="workflow-node__required">必要</span>}
      {node.traceStatus && <span className="workflow-node__trace">{node.traceStatus}</span>}
      {node.outputs.map((port, index) => <Handle type="source" position={Position.Right} id={port} key={port} style={{ top: 24 + index * 18 }} />)}
    </div>
  )
}
