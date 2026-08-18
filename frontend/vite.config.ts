import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// В деве фронт ходит на два сервиса напрямую; в проде тем же путям
// отвечает nginx (см. frontend/nginx.conf) — код приложения не меняется.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api/v1/tz': { target: 'http://localhost:8081', changeOrigin: true },
      '/api/v1': { target: 'http://localhost:8080', changeOrigin: true },
    },
  },
})
