import { CopilotKit } from '@copilotkit/react-core'
import { HttpAgent } from '@ag-ui/client'
import '@copilotkit/react-ui/styles.css'
import { useAuth } from './hooks/useAuth'
import AuthPage from './components/AuthPage'
import AppShell from './components/AppShell'
import './App.css'

// ponytail: POC 用瀏覽器直連 platform 的 AG-UI 端點(agents__unsafe_dev_only,官方標 dev-only)。
// 同源相對路徑經 Vite/nginx 的 /api 代理 → platform :8080,免 CORS。
// 上產線時換回官方 CopilotRuntime 橋接(~35 行 Node:CopilotRuntime + HttpAgent 註冊,見 CopilotKit docs)。
const platformAgent = new HttpAgent({ url: '/api/copilot/agui' })

/** 頂層路由:未登入 → AuthPage;已登入 → AppShell(側欄五視圖)。不用 react-router。 */
export default function App() {
  const { session, login, logout, register } = useAuth()

  if (!session) return <AuthPage login={login} register={register} />
  // 登入後才掛 CopilotKit;agent="platform" 對應上面直連註冊的 key。副駕與既有聊天視圖並存。
  return (
    <CopilotKit agents__unsafe_dev_only={{ platform: platformAgent }} agent="platform">
      <AppShell session={session} onLogout={logout} />
    </CopilotKit>
  )
}
