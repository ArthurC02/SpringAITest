import ELK from 'elkjs/lib/elk.bundled.js'
import type { WorkflowDefinition, WorkflowUiMetadata } from '../types'

const elk = new ELK()

/** ELK has no authority over semantic edges/nodes; it returns a positions-only metadata patch. */
export async function autoLayout(
  definition: WorkflowDefinition,
  metadata: WorkflowUiMetadata,
): Promise<WorkflowUiMetadata> {
  const graph = await elk.layout({
    id: 'workflow',
    layoutOptions: { 'elk.algorithm': 'layered', 'elk.direction': 'RIGHT', 'elk.spacing.nodeNode': '48' },
    children: definition.nodes.map((node) => ({ id: node.id, width: 190, height: 88 })),
    edges: definition.edges.map((edge) => ({ id: edge.id, sources: [edge.source.nodeId], targets: [edge.target.nodeId] })),
  })
  const positions = { ...metadata.positions }
  for (const child of graph.children ?? []) positions[child.id] = { x: child.x ?? 0, y: child.y ?? 0 }
  return { ...metadata, positions }
}
