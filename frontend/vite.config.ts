import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      // 開發時把 /api 轉發到 Spring 後端（backend/，預設 :8080）。
      // 瀏覽器只跟 Vite 同源溝通，因此完全不需要在後端開 CORS。
      '/api': {
        target: 'http://localhost:8080',
        changeOrigin: true,
      },
    },
  },
})
