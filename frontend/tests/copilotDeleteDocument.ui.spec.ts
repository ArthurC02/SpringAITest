import { randomUUID } from 'node:crypto'
import { expect, test, type Page, type Route } from '@playwright/test'

/**
 * AppShell's `deleteDocument` action is the only human-in-the-loop `renderAndWaitForResponse`
 * action in the app (see frontend/AGENTS.md: "confirm buttons are bare React handlers — always
 * call `respond()` on both success and failure, or the LLM run hangs"). Nothing else in the repo
 * exercises the real CopilotKit client through this two-path (confirm/cancel) contract, so this
 * file drives it against a fully mocked `/api/copilot/agui` using the same standard AG-UI
 * `data: ` frame shape platform's `CopilotAguiApiTests` proves against the real server
 * (RUN_STARTED -> TOOL_CALL_START/ARGS/END -> RUN_FINISHED). A regression here (e.g. a missing
 * `respond()` on either branch) shows up as the confirm dialog never reaching "已處理" and no
 * follow-up run ever being sent — exactly the hang the AGENTS.md warning describes.
 */

const docId = 'doc-hitl-1'
const docTitle = '待刪除文件'
const toolCallId = 'call-delete-1'
const parentMessageId = 'assistant-delete-1'

interface ToolWireMessage {
  role?: string
  content?: unknown
  toolCallId?: string
}

interface AgentRequestBody {
  threadId: string
  runId: string
  messages: ToolWireMessage[]
}

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

function sseFrame(payload: Record<string, unknown>): string {
  // Protocol-standard AG-UI frame: `data: ` with a space, deliberately unlike
  // `/api/chat/stream`'s hand-rolled `data:` (see root AGENTS.md).
  return `data: ${JSON.stringify(payload)}\n\n`
}

function toolCallStream(threadId: string, runId: string): string {
  return [
    sseFrame({ type: 'RUN_STARTED', threadId, runId }),
    sseFrame({ type: 'TOOL_CALL_START', toolCallId, toolCallName: 'deleteDocument', parentMessageId }),
    sseFrame({ type: 'TOOL_CALL_ARGS', toolCallId, delta: JSON.stringify({ id: docId }) }),
    sseFrame({ type: 'TOOL_CALL_END', toolCallId }),
    sseFrame({ type: 'RUN_FINISHED', threadId, runId }),
  ].join('')
}

function finishedStream(threadId: string, runId: string): string {
  return [sseFrame({ type: 'RUN_STARTED', threadId, runId }), sseFrame({ type: 'RUN_FINISHED', threadId, runId })].join('')
}

async function login(page: Page): Promise<void> {
  await page.goto('/')
  await page.getByTestId('auth-username').fill('user')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await expect(page.getByTestId('session-identity')).toBeVisible()
}

/**
 * Mocks the document list/delete endpoints and a deterministic `/api/copilot/agui`. The stream
 * only emits the `deleteDocument` tool call when the request's messages contain the caller's
 * unique `marker` (never on an unrelated/preparatory AG-UI POST), and only emits the no-op
 * finishing stream once the wire history already carries a tool result — mirroring how the real
 * deterministic evidence backend keys off exact prompt content instead of request order.
 */
function mockDeleteDocumentFlow(page: Page, marker: string) {
  const state = { deleteCalled: false, aguiRequests: [] as AgentRequestBody[] }
  page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo' })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/documents' && request.method() === 'GET') {
      return json(route, [
        { id: docId, title: docTitle, status: 'ready', chunk_count: 2, created_at: '2026-07-25T00:00:00Z' },
      ])
    }
    if (path === `/api/documents/${docId}` && request.method() === 'DELETE') {
      state.deleteCalled = true
      return route.fulfill({ status: 204 })
    }
    if (path === '/api/copilot/agui' && request.method() === 'POST') {
      const body = request.postDataJSON() as AgentRequestBody
      state.aguiRequests.push(body)
      const hasToolResult = body.messages.some((m) => m.role === 'tool' && Boolean(m.toolCallId))
      const isTrigger =
        !hasToolResult &&
        body.messages.some((m) => m.role === 'user' && typeof m.content === 'string' && m.content.includes(marker))
      const streamBody = isTrigger
        ? toolCallStream(body.threadId, body.runId)
        : finishedStream(body.threadId, `${body.runId}-noop`)
      return route.fulfill({ status: 200, contentType: 'text/event-stream', body: streamBody })
    }
    return json(route, [])
  })
  return state
}

async function openCopilotAndAskToDelete(page: Page, marker: string): Promise<void> {
  const sidebar = page.getByTestId('copilot-sidebar')
  await sidebar.locator('.copilotKitButton').click()
  await expect(sidebar.locator('.copilotKitWindow')).toHaveClass(/\bopen\b/)
  const input = sidebar.getByTestId('copilot-chat-textarea')
  await input.fill(`請刪除文件:${marker}`)
  await input.press('Enter')
}

function followUpToolResult(state: { aguiRequests: AgentRequestBody[] }): ToolWireMessage {
  const followUp = state.aguiRequests.find((r) => r.messages.some((m) => m.role === 'tool' && m.toolCallId === toolCallId))
  if (!followUp) throw new Error('No follow-up AG-UI request carried a matching tool result')
  return followUp.messages.find((m) => m.toolCallId === toolCallId)!
}

test.describe('AppShell deleteDocument human-in-the-loop confirm/cancel', () => {
  test('confirming deletes the document and respond()s with a success result', async ({ page }) => {
    const marker = `evidence-delete-${randomUUID()}`
    const state = mockDeleteDocumentFlow(page, marker)
    await login(page)
    await openCopilotAndAskToDelete(page, marker)

    const confirmDialog = page.locator('.copilot-confirm')
    await expect(confirmDialog).toContainText(`確認刪除文件「${docTitle}」`)
    await confirmDialog.getByRole('button', { name: '確認刪除', exact: true }).click()

    await expect.poll(() => state.deleteCalled).toBe(true)
    await expect(confirmDialog).toContainText('已處理')
    await expect
      .poll(() => state.aguiRequests.some((r) => r.messages.some((m) => m.role === 'tool' && m.toolCallId === toolCallId)))
      .toBe(true)
    expect(followUpToolResult(state).content).toBe('已刪除')
  })

  test('cancelling never deletes the document and respond()s with a cancellation result', async ({ page }) => {
    const marker = `evidence-delete-${randomUUID()}`
    const state = mockDeleteDocumentFlow(page, marker)
    await login(page)
    await openCopilotAndAskToDelete(page, marker)

    const confirmDialog = page.locator('.copilot-confirm')
    await expect(confirmDialog).toContainText(`確認刪除文件「${docTitle}」`)
    await confirmDialog.getByRole('button', { name: '取消', exact: true }).click()

    await expect
      .poll(() => state.aguiRequests.some((r) => r.messages.some((m) => m.role === 'tool' && m.toolCallId === toolCallId)))
      .toBe(true)
    expect(followUpToolResult(state).content).toBe('使用者取消刪除')
    expect(state.deleteCalled).toBe(false)
  })
})
