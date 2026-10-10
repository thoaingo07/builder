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
        // phones: dialogs take the whole screen; the body scrolls and the footer (primary action) stays put
        modal: {
          slots: {
            content: 'max-sm:inset-0 max-sm:translate-x-0 max-sm:translate-y-0 max-sm:w-full max-sm:max-w-none max-sm:h-dvh max-sm:max-h-none max-sm:rounded-none max-sm:ring-0',
            footer: 'max-sm:pb-[max(1rem,env(safe-area-inset-bottom))]',
          },
        },
        slideover: { slots: { content: 'max-sm:w-full max-sm:max-w-none' } },
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
