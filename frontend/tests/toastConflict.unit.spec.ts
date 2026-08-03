import { expect, test } from 'vitest'
import { requireLoaded, runWithToast } from '../src/components/Toast'
import { ApiError } from '../src/api/http'

/**
 * 假成功 toast 的根因在共通層:失敗一旦沒有傳到 runWithToast(呼叫端自行吞掉,或本層
 * 不認得樂觀併發衝突),成功 toast 照樣會噴出來。這裡直接釘住共通層的三條分支。
 */
function recorder() {
  const toasts: [string, string | undefined][] = []
  return { toasts, toast: (message: string, kind?: string) => { toasts.push([message, kind]) } }
}

test('409 conflict locks the editor and never shows a success toast', async () => {
  const { toast, toasts } = recorder()
  let locked = false
  await runWithToast(toast, () => Promise.reject(new ApiError(409, 'draft 版本衝突')), {
    success: '草稿已儲存',
    onConflict: () => { locked = true },
  })
  expect(locked).toBe(true)
  expect(toasts).toEqual([])
})

test('412 counts as the same optimistic-concurrency conflict', async () => {
  const { toast, toasts } = recorder()
  let locked = false
  await runWithToast(toast, () => Promise.reject(new ApiError(412, '已驗證版本不符')), {
    success: '已發布',
    onConflict: () => { locked = true },
  })
  expect(locked).toBe(true)
  expect(toasts).toEqual([])
})

test('a non-conflict failure keeps the plain error toast and never locks', async () => {
  const { toast, toasts } = recorder()
  let locked = false
  await runWithToast(toast, () => Promise.reject(new ApiError(500, '伺服器錯誤')), {
    success: '草稿已儲存',
    onConflict: () => { locked = true },
  })
  expect(locked).toBe(false)
  expect(toasts).toEqual([['伺服器錯誤', 'error']])
})

test('a caller without onConflict still surfaces the conflict as an error toast', async () => {
  const { toast, toasts } = recorder()
  await runWithToast(toast, () => Promise.reject(new ApiError(409, 'draft 版本衝突')), {
    success: '已回溯',
  })
  expect(toasts).toEqual([['draft 版本衝突', 'error']])
})

test('a missing write precondition is an error toast, never a silent success', async () => {
  const { toast, toasts } = recorder()
  // 舊寫法是 `if (etag) { ... }`:守衛不成立就靜默 return,runWithToast 只看得到「沒有拋錯」
  // 而噴出成功 toast。requireLoaded 讓 disabled 失守時變成明確錯誤。
  const etag: string | null = null
  await runWithToast(toast, async () => { await Promise.resolve(requireLoaded(etag, '草稿版本')) }, {
    success: '草稿已儲存',
  })
  expect(toasts).toEqual([['草稿版本尚未載入完成，請重新載入後再試。', 'error']])
})

test('success still toasts and runs onSuccess in order', async () => {
  const { toast, toasts } = recorder()
  const order: string[] = []
  await runWithToast(toast, () => { order.push('call'); return Promise.resolve('ok') }, {
    success: '草稿已儲存',
    onSuccess: (result) => { order.push(`after:${result}`) },
    onConflict: () => order.push('conflict'),
  })
  expect(order).toEqual(['call', 'after:ok'])
  expect(toasts).toEqual([['草稿已儲存', 'success']])
})
