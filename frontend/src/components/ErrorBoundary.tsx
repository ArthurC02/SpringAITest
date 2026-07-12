import { Component, type ErrorInfo, type ReactNode } from 'react'

interface Props {
  children: ReactNode
}
interface State {
  hasError: boolean
}

/** 包住視圖區的錯誤邊界：任一視圖 render 崩潰時顯示 fallback + 重新載入，不整站白屏。 */
export default class ErrorBoundary extends Component<Props, State> {
  state: State = { hasError: false }

  static getDerivedStateFromError(): State {
    return { hasError: true }
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error('視圖發生未預期錯誤：', error, info)
  }

  render(): ReactNode {
    if (this.state.hasError) {
      return (
        <div className="view" role="alert">
          <h2 className="view__title">畫面發生錯誤</h2>
          <p className="muted">很抱歉，這個畫面出了點問題。</p>
          <button className="btn btn--primary" onClick={() => window.location.reload()}>
            重新載入
          </button>
        </div>
      )
    }
    return this.props.children
  }
}
