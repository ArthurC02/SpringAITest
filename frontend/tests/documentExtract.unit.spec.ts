import { describe, expect, it, vi } from 'vitest'

// pdf.js/mammoth 都是動態 import(),vi.mock 依模組 id 攔截即可涵蓋(見 documentExtract.ts)。
vi.mock('pdfjs-dist', () => ({
  GlobalWorkerOptions: {},
  getDocument: vi.fn(),
}))
vi.mock('pdfjs-dist/build/pdf.worker.mjs?url', () => ({ default: 'worker-url-stub' }))
vi.mock('mammoth', () => ({ extractRawText: vi.fn() }))

import { extractDocumentText } from '../src/documentExtract'
import { getDocument } from 'pdfjs-dist'
import { extractRawText } from 'mammoth'

function fakePdfDoc(pagesItems: Array<Array<{ str: string }>>) {
  return {
    numPages: pagesItems.length,
    getPage: (pageNumber: number) =>
      Promise.resolve({
        getTextContent: () => Promise.resolve({ items: pagesItems[pageNumber - 1] }),
      }),
  }
}

function pdfFile(name = 'report.pdf') {
  return new File([new Uint8Array([1, 2, 3])], name, { type: 'application/pdf' })
}

describe('extractDocumentText — .pdf', () => {
  it('串接多頁文字內容', async () => {
    vi.mocked(getDocument).mockReturnValue({
      promise: Promise.resolve(fakePdfDoc([[{ str: '第一頁' }], [{ str: '第二頁' }]])),
    } as ReturnType<typeof getDocument>)

    const result = await extractDocumentText(pdfFile())

    expect(result).toEqual({ text: '第一頁\n第二頁' })
  })

  it('抽出文字為空白時回報「掃描版 PDF」錯誤,不是靜默塞空字串', async () => {
    vi.mocked(getDocument).mockReturnValue({
      promise: Promise.resolve(fakePdfDoc([[{ str: '   ' }]])),
    } as ReturnType<typeof getDocument>)

    const result = await extractDocumentText(pdfFile())

    expect(result).toEqual({
      errorMessage: '這份 PDF 沒有可擷取的文字（可能是掃描影像），請先轉成文字檔再上傳。',
    })
  })

  it('解析拋錯時回報通用讀取失敗訊息', async () => {
    vi.mocked(getDocument).mockImplementation(() => {
      throw new Error('corrupt pdf')
    })

    const result = await extractDocumentText(pdfFile())

    expect(result).toEqual({
      errorMessage: '無法讀取這個檔案，請確認格式或先另存為純文字。',
    })
  })
})

describe('extractDocumentText — .docx', () => {
  it('回傳 mammoth 抽出的純文字', async () => {
    vi.mocked(extractRawText).mockResolvedValue({ value: '合約內容', messages: [] })

    const result = await extractDocumentText(new File([new Uint8Array([1])], 'contract.docx'))

    expect(result).toEqual({ text: '合約內容' })
  })

  it('解析拋錯時回報通用讀取失敗訊息', async () => {
    vi.mocked(extractRawText).mockRejectedValue(new Error('bad zip'))

    const result = await extractDocumentText(new File([new Uint8Array([1])], 'contract.docx'))

    expect(result).toEqual({
      errorMessage: '無法讀取這個檔案，請確認格式或先另存為純文字。',
    })
  })
})
