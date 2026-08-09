import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  ApiError,
  apiFetch,
  consumeSessionExpired,
  parseErrorMessage,
  setLogoutHandler,
} from '../src/api/http'

function storedSession(token: string): string {
  return JSON.stringify({
    token,
    username: token,
    role: 'USER',
    tenantCode: 'demo',
    capabilities: [],
  })
}

function delayedJsonResponse(status: number) {
  let release!: () => void
  const released = new Promise<void>((resolve) => { release = resolve })
  const response = new Response(new ReadableStream({
    async start(controller) {
      await released
      controller.enqueue(new TextEncoder().encode(JSON.stringify({ message: 'unauthorized' })))
      controller.close()
    },
  }), { status, headers: { 'Content-Type': 'application/json' } })
  return { response, release }
}

// workflow 的固定安全訊息字面上就叫使用者「提供追蹤編號給管理員」,所以 5xx 的訊息
// 一定要真的把編號印出來;4xx 則刻意不附加(404 偽裝契約:不得在該路徑上多給資訊)。
describe('parseErrorMessage 追蹤編號', () => {
  const SERVER_MESSAGE = '工作流程執行失敗,請提供追蹤編號給管理員'

  function errorResponse(
    status: number,
    body: Record<string, unknown>,
    headers: Record<string, string> = {},
  ): Response {
    return new Response(JSON.stringify(body), {
      status,
      headers: { 'Content-Type': 'application/json', ...headers },
    })
  }

  it('appends the body correlationId on 5xx', async () => {
    const res = errorResponse(500, { message: SERVER_MESSAGE, correlationId: '0HN7A1B2C3D4E' })
    expect(await parseErrorMessage(res, 'fallback')).toBe(`${SERVER_MESSAGE}(追蹤編號:0HN7A1B2C3D4E)`)
  })

  it('falls back to the X-Correlation-Id header when the body has none', async () => {
    const res = errorResponse(502, { message: SERVER_MESSAGE }, { 'X-Correlation-Id': 'from-header' })
    expect(await parseErrorMessage(res, 'fallback')).toBe(`${SERVER_MESSAGE}(追蹤編號:from-header)`)
  })

  it('returns the plain message when neither body nor header carries an id', async () => {
    const res = errorResponse(500, { message: SERVER_MESSAGE })
    expect(await parseErrorMessage(res, 'fallback')).toBe(SERVER_MESSAGE)
  })

  it('uses the fallback text when the 5xx body is not an ApiError JSON', async () => {
    const res = new Response('<html>gateway</html>', {
      status: 503,
      headers: { 'X-Correlation-Id': 'gw-1' },
    })
    expect(await parseErrorMessage(res, '請求失敗（HTTP 503）')).toBe('請求失敗（HTTP 503）(追蹤編號:gw-1)')
  })

  it.each([400, 401, 404, 409])('does not append on %i even when the id is present', async (status) => {
    const res = errorResponse(status, { message: '帳號或密碼錯誤', correlationId: 'x-1' }, { 'X-Correlation-Id': 'x-1' })
    expect(await parseErrorMessage(res, 'fallback')).toBe('帳號或密碼錯誤')
  })

  it.each([
    ['blank', '   '],
    ['over 128 characters', 'a'.repeat(129)],
    ['a newline', "abc\ndef"],
    ['a NUL', `abc${String.fromCharCode(0)}def`],
    ['a DEL', `abc${String.fromCharCode(127)}def`],
  ])('drops an id with %s', async (_label, id) => {
    const res = errorResponse(500, { message: SERVER_MESSAGE, correlationId: id })
    expect(await parseErrorMessage(res, 'fallback')).toBe(SERVER_MESSAGE)
  })

  it('keeps an id of exactly 128 characters', async () => {
    const id = 'b'.repeat(128)
    const res = errorResponse(500, { message: SERVER_MESSAGE, correlationId: id })
    expect(await parseErrorMessage(res, 'fallback')).toBe(`${SERVER_MESSAGE}(追蹤編號:${id})`)
  })
})

describe('apiFetch 401 session fencing', () => {
  let sessionJson: string | null

  beforeEach(() => {
    consumeSessionExpired()
    sessionJson = storedSession('old-token')
    vi.stubGlobal('localStorage', {
      getItem: () => sessionJson,
      setItem: vi.fn(),
      removeItem: vi.fn(),
    })
  })

  afterEach(() => {
    setLogoutHandler(null)
    consumeSessionExpired()
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('does not let an old account delayed-body 401 log out the new account', async () => {
    const delayed = delayedJsonResponse(401)
    vi.stubGlobal('fetch', vi.fn(async () => delayed.response))
    const logout = vi.fn()
    setLogoutHandler(logout)

    const outcome = apiFetch('/api/protected').catch((error) => error)
    await vi.waitFor(() => expect(fetch).toHaveBeenCalledOnce())
    sessionJson = storedSession('new-token')
    delayed.release()

    expect(await outcome).toBeInstanceOf(ApiError)
    expect(logout).not.toHaveBeenCalled()
    expect(consumeSessionExpired()).toBe(false)
  })

  it('still logs out when the delayed 401 belongs to the current session', async () => {
    const delayed = delayedJsonResponse(401)
    vi.stubGlobal('fetch', vi.fn(async () => delayed.response))
    const logout = vi.fn()
    setLogoutHandler(logout)

    const outcome = apiFetch('/api/protected').catch((error) => error)
    await vi.waitFor(() => expect(fetch).toHaveBeenCalledOnce())
    delayed.release()

    expect(await outcome).toBeInstanceOf(ApiError)
    expect(logout).toHaveBeenCalledOnce()
    expect(consumeSessionExpired()).toBe(true)
  })
})
