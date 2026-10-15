import { defineConfig, devices } from '@playwright/test'

const PREVIEW_PORT = 4173
const BASE_URL = `http://127.0.0.1:${String(PREVIEW_PORT)}`
const isCi = process.env.CI !== undefined

// O smoke roda contra o bundle de produção (vite build + preview), sem backend nem TLS:
// o que se verifica aqui é a SPA estática. Proxy e HTTPS local valem só para `pnpm dev`.
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: isCi,
  retries: isCi ? 1 : 0,
  reporter: isCi ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: BASE_URL,
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'webkit', use: { ...devices['Desktop Safari'] } },
    { name: 'mobile-chromium', use: { ...devices['Pixel 7'] } },
  ],
  webServer: {
    // Sem "pnpm" aqui: o pnpm vem do Corepack e pode não estar no PATH; o script e2e já põe node_modules/.bin no PATH.
    command: `vite build && vite preview --host 127.0.0.1 --port ${String(PREVIEW_PORT)} --strictPort`,
    url: BASE_URL,
    reuseExistingServer: !isCi,
    timeout: 120_000,
  },
})
