import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, apiFetch, consumeSessionExpired, setLogoutHandler } from '../src/api/http'

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
