/**
 * `.pdf`/`.docx` 瀏覽器內抽字（WS2-a，見 plans/ux-core-journey/02-spec.md §2.2a）。
 * 抽出結果沿用既有 `create(title, text)` 路徑，backend ingest 契約完全不變。
 * `.txt`/`.md` 不經過這裡 — 呼叫端（DocumentsView.onFile）仍直接用 `File.text()`。
 *
 * pdf.js 與 mammoth 都用動態 `import()` 懶載入：只有選到對應副檔名才把它們拉進 bundle，
 * 不會出現在主 chunk 裡。pdf.js 的 worker 用官方支援的 Vite bundler 慣例
 * （`?url` 靜態資產匯入，見 https://mozilla.github.io/pdf.js/examples/ 的 bundler 範例），
 * 檔案本身仍由建置產物自帶，不從 CDN 載。
 */

export type ExtractResult = { text: string } | { errorMessage: string }

const EMPTY_PDF_TEXT_ERROR =
  '這份 PDF 沒有可擷取的文字（可能是掃描影像），請先轉成文字檔再上傳。'
const READ_FAILED_ERROR = '無法讀取這個檔案，請確認格式或先另存為純文字。'

async function extractPdfText(file: File): Promise<string> {
  const [pdfjsLib, workerUrl] = await Promise.all([
    import('pdfjs-dist'),
    import('pdfjs-dist/build/pdf.worker.mjs?url'),
  ])
  pdfjsLib.GlobalWorkerOptions.workerSrc = workerUrl.default
  const data = await file.arrayBuffer()
  const pdf = await pdfjsLib.getDocument({ data }).promise
  const pages: string[] = []
  for (let pageNumber = 1; pageNumber <= pdf.numPages; pageNumber += 1) {
    const page = await pdf.getPage(pageNumber)
    const content = await page.getTextContent()
    pages.push(content.items.map((item) => ('str' in item ? item.str : '')).join(''))
  }
  return pages.join('\n')
}

async function extractDocxText(file: File): Promise<string> {
  // mammoth 的型別是 `export =`（CJS），這裡只用到 extractRawText，直接宣告用到的形狀，
  // 不糾結 esModuleInterop 細節。
  const mammoth = (await import('mammoth')) as unknown as {
    extractRawText(input: { arrayBuffer: ArrayBuffer }): Promise<{ value: string }>
  }
  const arrayBuffer = await file.arrayBuffer()
  const { value } = await mammoth.extractRawText({ arrayBuffer })
  return value
}

/** 呼叫端只在使用者選到 `.pdf`/`.docx` 時呼叫；依副檔名分派並統一處理兩種失敗路徑：
 *  抽出文字為空/僅空白（多半是掃描版 PDF，無文字層）與解析拋錯（毀損檔案、不支援的內容）。 */
export async function extractDocumentText(file: File): Promise<ExtractResult> {
  const isPdf = /\.pdf$/i.test(file.name)
  try {
    const text = isPdf ? await extractPdfText(file) : await extractDocxText(file)
    if (!text.trim()) {
      return { errorMessage: isPdf ? EMPTY_PDF_TEXT_ERROR : READ_FAILED_ERROR }
    }
    return { text }
  } catch {
    return { errorMessage: READ_FAILED_ERROR }
  }
}
