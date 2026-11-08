import path from 'node:path'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Served by the Host under /ui/. In dev, the API and the hub are proxied to the local Host.
const host = process.env.CHAMELEON_URL ?? 'http://localhost:5000'

export default defineConfig({
  base: '/ui/',
  plugins: [react()],
  resolve: { alias: { '@': path.resolve(__dirname, './src') } },
  // Straight into the Host: `pnpm build` then `dotnet run` serves the panel at /ui.
  build: { outDir: '../src/Chameleon.Host/wwwroot/ui', emptyOutDir: true, chunkSizeWarningLimit: 800 },
  server: {
    proxy: {
      '/_chameleon': { target: host, ws: true },
      '/s/': { target: host },
    },
  },
})
