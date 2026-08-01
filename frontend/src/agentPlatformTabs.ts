export type AgentPlatformTab = 'agents' | 'workflows' | 'orchestrators'

interface AgentPlatformGates {
  isAdmin: boolean
  agentBuilderEnabled: boolean
  workflowDesignerEnabled: boolean
  canManageWorkflow: boolean
}

export function agentPlatformTabs(gates: AgentPlatformGates): AgentPlatformTab[] {
  return [
    ...(gates.agentBuilderEnabled && gates.isAdmin ? (['agents'] as const) : []),
    ...(gates.workflowDesignerEnabled && gates.canManageWorkflow
      ? (['workflows', 'orchestrators'] as const)
      : []),
  ]
}
