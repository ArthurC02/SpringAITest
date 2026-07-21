import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

function resolveLoopbackApiProxyTarget(value: string | undefined): string {
  const fallback = 'http://localhost:8080'
  if (!value) return fallback

  let target: URL
  try {
    target = new URL(value)
  } catch {
    throw new Error('VITE_API_PROXY_TARGET must be an absolute loopback HTTP URL')
  }

  const loopbackHosts = new Set(['localhost', '127.0.0.1', '[::1]'])
  if (
    target.protocol !== 'http:' ||
    !loopbackHosts.has(target.hostname) ||
    target.username ||
    target.password ||
    target.pathname !== '/' ||
    target.search ||
    target.hash
  ) {
    throw new Error('VITE_API_PROXY_TARGET must be an origin-only loopback HTTP URL')
  }

  return target.origin
}

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const apiProxyTarget = resolveLoopbackApiProxyTarget(env.VITE_API_PROXY_TARGET)

  return {
    plugins: [react()],
    server: {
      port: 5173,
      proxy: {
        // 開發時把 /api 轉發到 Spring 後端（platform/，預設 :8080）。
        // Evidence may safely override this to its loopback-only isolated platform.
        // 副駕的 AG-UI(/api/copilot/agui,SSE)同樣走這條。
        // 瀏覽器只跟 Vite 同源溝通，因此完全不需要在後端開 CORS。
        '/api': {
          target: apiProxyTarget,
          changeOrigin: true,
        },
      },
    },
  }
})
