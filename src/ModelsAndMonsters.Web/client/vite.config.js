import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The API host runs on :5170; the CORS policy there allows this dev origin.
export default defineConfig({
  plugins: [react()],
  server: { port: 5173 }
})
