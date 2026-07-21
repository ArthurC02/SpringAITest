import { expect, test } from '@playwright/test'
import { evidenceUsers, signIn } from './helpers/auth'

const toolPrompt = process.env.EVIDENCE_TOOL_PROMPT

interface ToolWireMessage {
  id?: string
  role?: string
  content?: unknown
  toolCallId?: string
  toolCalls?: Array<{ id?: string; function?: { name?: string; arguments?: string } }>
}

interface AgentRequest {
  messages: ToolWireMessage[]
  tools: Array<{ name?: string; function?: { name?: string } }>
}

/**
 * E-03 deliberately drives the real browser CopilotKit client. The deterministic
 * model selects `switchView` by name and passes the fixed `documents` argument.
 * The existing handler changes only in-page view state. Its result is inspected
 * in memory and is never attached to the bundle.
 */
test.describe('E-03 browser client-tool evidence', () => {
  test('CopilotKit returns one logical result for the matching client tool call', async ({ page }) => {
    test.skip(!toolPrompt, 'EVIDENCE_TOOL_PROMPT is required; outer evidence gate must mark BLOCKED')

    const requests: AgentRequest[] = []
    page.on('request', (request) => {
      if (!request.url().includes('/api/copilot/agui') || request.method() !== 'POST') return
      try {
        const payload = request.postDataJSON() as AgentRequest
        requests.push({ messages: payload.messages ?? [], tools: payload.tools ?? [] })
      } catch {
        // The terminal expectation below reports an ordinary test failure. Do not put
        // request data in the error: it can contain the browser bearer token.
      }
    })

    await signIn(page, evidenceUsers.a)
    const sidebar = page.getByTestId('copilot-sidebar')
    const window = sidebar.locator('.copilotKitWindow')
    await sidebar.locator('.copilotKitButton').click()
    await expect(window).toHaveClass(/\bopen\b/)
    const input = sidebar.getByTestId('copilot-chat-textarea')
    const finalRunResponse = page.waitForResponse((response) => {
      if (!response.url().includes('/api/copilot/agui') || response.request().method() !== 'POST') return false
      try {
        const payload = response.request().postDataJSON() as AgentRequest
        return (payload.messages ?? []).some(
          (message) => message.role === 'tool' && Boolean(message.toolCallId),
        )
      } catch {
        return false
      }
    })
    await input.fill(toolPrompt!)
    await input.press('Enter')

    // HttpAgent may send a preparatory request before its run request. The latter
    // must advertise the existing harmless action.
    await expect.poll(() => requests.some((request) => request.tools.length > 0)).toBeTruthy()
    const initial = requests.find((request) => request.tools.length > 0)!
    const toolNames = initial.tools.map((tool) => tool.function?.name ?? tool.name)
    expect(toolNames).toContain('switchView')

    let toolCalls: Array<{ id: string; name: string }> = []
    let results: ToolWireMessage[] = []
    await expect
      .poll(() => {
        // CopilotKit legitimately resends the full array across requests. Count
        // logical messages in the latest array containing the tool result, not
        // duplicate wire observations of the same call id.
        const latest = requests.findLast((request) =>
          request.messages.some((message) => message.role === 'tool' && Boolean(message.toolCallId)),
        )
        if (!latest) return 0
        toolCalls = latest.messages.flatMap((message) =>
          (message.toolCalls ?? [])
            .filter((call): call is { id: string; function?: { name?: string } } => Boolean(call.id))
            .map((call) => ({ id: call.id, name: call.function?.name ?? '' })),
        )
        results = latest.messages.filter((message) => message.role === 'tool' && Boolean(message.toolCallId))
        return results.length
      })
      .toBeGreaterThan(0)

    const switchCalls = [
      ...new Map(
        toolCalls.filter((call) => call.name === 'switchView').map((call) => [call.id, call]),
      ).values(),
    ]
    expect(switchCalls).toHaveLength(1)
    const callId = switchCalls[0].id
    // Do not deduplicate wire results: repeated logical results must fail even
    // when they carry identical content.
    const matchingResults = results.filter((message) => message.toolCallId === callId)
    // The matching return plus rendered state proves that the registered action
    // handled the call. This intentionally claims one logical wire result, not
    // an internal invocation count that CopilotKit does not expose.
    expect(matchingResults).toHaveLength(1)
    expect(matchingResults[0].content).toBe('已切換到「documents」視圖。')
    await expect(page.getByTestId('nav-documents')).toHaveAttribute('aria-current', 'page')
    expect(results.every((message) => toolCalls.some((call) => call.id === message.toolCallId))).toBeTruthy()
    expect((await finalRunResponse).status()).toBe(200)
  })
})
