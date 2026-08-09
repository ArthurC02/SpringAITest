import { expect, test, type Page, type Route } from '@playwright/test'
import { zipSync, strToU8 } from 'fflate'

// WS2-a 瀏覽器內抽字（見 documentExtract.ts / DocumentsView.onFile）：這裡跑真的 pdf.js／mammoth
// （dev server 沒有 mock JS 模組的機制,真的餵一個手刻的最小 .docx／.pdf 進去,讓實際函式庫解析）,
// 只 mock API route。三個案例對應 §2.2a 的兩條失敗路徑 + 抽字中 disabled。

const CONTENT_TYPES = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
</Types>`

const ROOT_RELS = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>`

const DOCUMENT_XML = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>
    <w:p><w:r><w:t>合約內容第一段</w:t></w:r></w:p>
  </w:body>
</w:document>`

function minimalDocxBuffer(): Buffer {
  const zipped = zipSync({
    '[Content_Types].xml': strToU8(CONTENT_TYPES),
    '_rels/.rels': strToU8(ROOT_RELS),
    'word/document.xml': strToU8(DOCUMENT_XML),
  })
  return Buffer.from(zipped)
}

// 沒有 xref table 的最小 PDF：pdf.js 會用它的物件掃描 fallback 解析出來,頁面本身沒有任何
// 文字內容物件,`getTextContent()` 會回傳空陣列 —— 對應「掃描版 PDF,無文字層」的失敗路徑。
function minimalEmptyPdfBuffer(): Buffer {
  const pdf = `%PDF-1.4
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
2 0 obj
<< /Type /Pages /Kids [3 0 R] /Count 1 >>
endobj
3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents 4 0 R >>
endobj
4 0 obj
<< /Length 0 >>
stream
endstream
endobj
trailer
<< /Size 5 /Root 1 0 R >>
%%EOF
`
  return Buffer.from(pdf, 'utf-8')
}

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function openDocuments(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/documents' && route.request().method() === 'GET') return json(route, [])
    return json(route, [])
  })
  await page.goto('/')
  await page.getByTestId('auth-username').fill('user')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-documents').click()
}

test('選 .docx 成功抽出文字並填入內容欄', async ({ page }) => {
  let createBody: { title?: string; text?: string } | null = null
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (!path.startsWith('/api/')) return route.continue()
    if (path === '/api/auth/login') {
      return json(route, { token: 'token', username: 'user', role: 'USER', tenantCode: 'demo', capabilities: [] })
    }
    if (path === '/api/features') return json(route, {})
    if (path === '/api/documents' && request.method() === 'GET') return json(route, [])
    if (path === '/api/documents' && request.method() === 'POST') {
      createBody = JSON.parse(request.postData() ?? '{}')
      return json(route, { id: 'doc-1', title: 'contract', status: 'processing' }, 202)
    }
    return json(route, [])
  })
  await page.goto('/')
  await page.getByTestId('auth-username').fill('user')
  await page.getByTestId('auth-password').fill('password123')
  await page.getByTestId('auth-submit').click()
  await page.getByTestId('nav-documents').click()

  await page.getByLabel(/檔案/).setInputFiles({
    name: 'contract.docx',
    mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    buffer: minimalDocxBuffer(),
  })

  // 檔案模式的文字內容不直接可見（只有切到「貼上文字」才會渲染 textarea,但切換模式會照既有
  // 設計清空內容,見 switchMode）,改用已讀入字數的提示與實際送出的 request body 驗證抽字成功。
  await expect(page.getByText(/已讀入 \d+ 字/)).toBeVisible()
  await expect(page.getByLabel('標題')).toHaveValue('contract')
  await page.getByRole('button', { name: '新增文件' }).click()

  await expect.poll(() => createBody).not.toBeNull()
  expect((createBody as unknown as { text: string }).text).toContain('合約內容第一段')
})

test('掃描版 PDF（無文字層）顯示明確的 inline 錯誤，不塞空白進表單', async ({ page }) => {
  await openDocuments(page)

  await page.getByLabel(/檔案/).setInputFiles({
    name: 'scanned.pdf',
    mimeType: 'application/pdf',
    buffer: minimalEmptyPdfBuffer(),
  })

  await expect(
    page.getByText('這份 PDF 沒有可擷取的文字（可能是掃描影像），請先轉成文字檔再上傳。'),
  ).toBeVisible()
  await page.getByLabel('標題').fill('掃描文件')
  await page.getByRole('button', { name: '新增文件' }).click()
  // 空文字仍卡在既有的「請選擇檔案」驗證上,不會被送出。
  await expect(page.getByText('請選擇檔案')).toBeVisible()
})

test('抽字進行中按鈕停用並顯示「讀取檔案中…」', async ({ page }) => {
  await openDocuments(page)
  // mammoth 是動態 import(),小檔案在本機可能解析得比 Playwright 第一次輪詢還快——
  // 刻意拖慢這次模組請求,讓「抽字中」這個過渡態有足夠時間被斷言到,不是靠運氣。
  await page.route('**/*mammoth*', async (route) => {
    await new Promise((resolve) => setTimeout(resolve, 500))
    await route.continue()
  })

  await page.getByLabel(/檔案/).setInputFiles({
    name: 'contract.docx',
    mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    buffer: minimalDocxBuffer(),
  })

  const submitButton = page.getByRole('button', { name: /新增文件|讀取檔案中/ })
  // 抽字是 onFile 裡第一個 await 之前就會同步切換的狀態,選檔後應立刻能觀察到停用態。
  await expect(submitButton).toBeDisabled()
  await expect(submitButton).toHaveText('讀取檔案中…')

  await expect(submitButton).toBeEnabled()
  await expect(submitButton).toHaveText('新增文件')
})
