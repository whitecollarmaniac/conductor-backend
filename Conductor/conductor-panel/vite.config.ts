import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// https://vite.dev/config/
export default defineConfig(({ mode }) => ({
  plugins: [vue()],
  esbuild: mode === 'production' ? { drop: ['console', 'debugger'] } : {},
}))
