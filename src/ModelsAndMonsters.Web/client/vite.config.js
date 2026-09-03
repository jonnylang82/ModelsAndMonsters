import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Keep browser requests same-origin in development too. Never send a phone to its own localhost.
export default defineConfig({
  plugins: [react()],
  server: { port: 5173, proxy: { '/api': 'http://127.0.0.1:5170' } }
})
