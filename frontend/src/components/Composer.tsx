import { useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react'

interface Props {
  disabled: boolean
  onSend: (text: string) => void
}

export default function Composer({ disabled, onSend }: Props) {
  const [input, setInput] = useState('')
  const taRef = useRef<HTMLTextAreaElement>(null)

  // 自動長高：先把高度歸零，再依內容 scrollHeight 撐開（上限由 CSS max-height 控制）。
  useLayoutEffect(() => {
    const ta = taRef.current
    if (!ta) return
    ta.style.height = 'auto'
    ta.style.height = `${ta.scrollHeight}px`
  }, [input])

  function submit() {
    const text = input.trim()
    if (!text || disabled) return
    onSend(text)
    setInput('')
  }

  function handleKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      submit()
    }
  }

  return (
    <div className="composer">
      <textarea
        ref={taRef}
        className="composer__input"
        placeholder="輸入訊息，Enter 送出、Shift+Enter 換行…"
        value={input}
        onChange={(e) => setInput(e.target.value)}
        onKeyDown={handleKeyDown}
        rows={1}
      />
      <button
        className="composer__send"
        onClick={submit}
        disabled={disabled || !input.trim()}
      >
        送出
      </button>
    </div>
  )
}
