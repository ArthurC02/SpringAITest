import { useEffect, useRef } from 'react'
import { basicSetup, EditorView } from 'codemirror'
import { python } from '@codemirror/lang-python'

interface Props {
  value: string
  onChange?: (v: string) => void
  readOnly?: boolean
}

/**
 * CodeMirror 6 Python 編輯器。只透過 lazy(() => import('./PythonEditor')) 動態載入，
 * 不進首屏 bundle（規格 O5 / SSR-P2C-001）。YAML 欄不換 CodeMirror，沿用既有 YamlEditor。
 * ponytail: CodeMirror 只給 Python；Monaco 過重不採。
 */
export default function PythonEditor({ value, onChange, readOnly }: Props) {
  const host = useRef<HTMLDivElement>(null)
  const viewRef = useRef<EditorView | null>(null)

  useEffect(() => {
    if (!host.current) return
    const view = new EditorView({
      doc: value,
      parent: host.current,
      extensions: [
        basicSetup,
        python(),
        EditorView.editable.of(!readOnly),
        EditorView.updateListener.of((u) => {
          if (u.docChanged) onChange?.(u.state.doc.toString())
        }),
      ],
    })
    viewRef.current = view
    return () => {
      view.destroy()
      viewRef.current = null
    }
    // 只在掛載時建立一次；外部 value 變動由下方 effect 同步。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // 外部 value 變動（例如切換範本後重置規則）→ 同步進編輯器，避免與內部游標互踩。
  useEffect(() => {
    const view = viewRef.current
    if (!view) return
    const current = view.state.doc.toString()
    if (current !== value) {
      view.dispatch({ changes: { from: 0, to: current.length, insert: value } })
    }
  }, [value])

  return <div className="cm-python" ref={host} />
}
