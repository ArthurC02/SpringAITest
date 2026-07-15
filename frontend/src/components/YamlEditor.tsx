import { useMemo, useRef, type Ref } from 'react'
import hljs from 'highlight.js/lib/core'
import yamlLang from 'highlight.js/lib/languages/yaml'
import 'highlight.js/styles/github-dark.css'

hljs.registerLanguage('yaml', yamlLang)

interface Props {
  value: string
  onChange?: (v: string) => void
  readOnly?: boolean
  label: string
  /** 給呼叫端做「游標處插入節點樣板」用（NodeCatalog 點擊插入）。 */
  ref?: Ref<HTMLTextAreaElement>
}

/**
 * YAML 編輯器 = 透明 textarea 疊在 highlight.js 上色層之上（highlight.js 已是既有相依，
 * 見 Markdown.tsx）。捲動時同步上色層的 scrollTop/Left，兩層字體度量必須一致，
 * 樣式集中在 .yaml__hl / .yaml__input 的共用宣告，改一邊要一起改。
 * ponytail: 沒有行號欄與自動縮排；驗證錯誤以「第 N 行」文字指位。要真編輯器再談 CodeMirror（需授權）。
 */
export default function YamlEditor({ value, onChange, readOnly, label, ref }: Props) {
  const preRef = useRef<HTMLPreElement>(null)
  // 尾端補換行：最後一行是空行時上色層才撐得出高度，與 textarea 對齊。
  const html = useMemo(
    () => hljs.highlight(`${value}\n`, { language: 'yaml' }).value,
    [value],
  )

  return (
    <div className="yaml">
      <pre className="yaml__hl hljs" ref={preRef} aria-hidden="true">
        {/* 內容是使用者自己的 YAML，且 highlight.js 會轉義，不構成 XSS。 */}
        <code dangerouslySetInnerHTML={{ __html: html }} />
      </pre>
      <textarea
        ref={ref}
        className="yaml__input"
        value={value}
        readOnly={readOnly}
        spellCheck={false}
        aria-label={label}
        onChange={(e) => onChange?.(e.target.value)}
        onScroll={(e) => {
          const p = preRef.current
          if (!p) return
          p.scrollTop = e.currentTarget.scrollTop
          p.scrollLeft = e.currentTarget.scrollLeft
        }}
      />
    </div>
  )
}
