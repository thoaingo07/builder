import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import ui from '@nuxt/ui/vite'

const bff = process.env.BUILDER_BFF_URL ?? 'http://localhost:19000'

export default defineConfig({
  plugins: [
    vue(),
    ui({
      ui: {
        colors: { primary: 'indigo', neutral: 'slate' },
        // cards with overflow-hidden must not be squashed by the panel's flex column
        dashboardPanel: { slots: { body: '*:shrink-0' } },
      },
    }),
  ],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  server: {
    port: Number(process.env.PORT) || 19001,
    strictPort: true,
    proxy: {
      '/api': { target: bff, changeOrigin: false },
      '/bff': { target: bff, changeOrigin: false },
      '/hubs': { target: bff, changeOrigin: false, ws: true },
    },
  },
  build: { outDir: 'dist', emptyOutDir: true, chunkSizeWarningLimit: 2000 },
})
