import type { Connection, Edge } from '@xyflow/react'
import type { WorkflowDefinition, WorkflowNodeType } from '../types'

export function canConnect(
  connection: Connection | Edge,
  definition: WorkflowDefinition,
  catalog: WorkflowNodeType[],
): boolean {
  if (!connection.source || !connection.target || !connection.sourceHandle || !connection.targetHandle) return false
  if (connection.source === connection.target) return false
  const sourceNode = definition.nodes.find((node) => node.id === connection.source)
  const targetNode = definition.nodes.find((node) => node.id === connection.target)
  if (!sourceNode || !targetNode) return false
  const sourceType = catalog.find((entry) => entry.type === sourceNode.type && entry.version === sourceNode.typeVersion)
  const targetType = catalog.find((entry) => entry.type === targetNode.type && entry.version === targetNode.typeVersion)
  const sourcePort = sourceType?.outputs.find((port) => port.id === connection.sourceHandle)
  const targetPort = targetType?.inputs.find((port) => port.id === connection.targetHandle)
  if (!sourcePort || !targetPort || sourcePort.dataType !== targetPort.dataType) return false
  const sameEdge = definition.edges.some((edge) =>
    edge.source.nodeId === connection.source && edge.source.port === connection.sourceHandle
    && edge.target.nodeId === connection.target && edge.target.port === connection.targetHandle)
  if (sameEdge) return false
  const connections = definition.edges.filter((edge) => edge.target.nodeId === connection.target && edge.target.port === connection.targetHandle).length
  return targetPort.maxConnections == null || connections < targetPort.maxConnections
}
