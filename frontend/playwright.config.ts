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
  reporter: isCi ? [['list', { printFailuresInline: true }], ['github'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: BASE_URL,
    // O idioma da interface segue o navegador; sem isto o Playwright anuncia en-US. O inglês tem spec própria.
    locale: 'pt-BR',
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'chromium', testIgnore: '**/edge-csp-probe.spec.ts', use: { ...devices['Desktop Chrome'] } },
    { name: 'webkit', testIgnore: '**/edge-csp-probe.spec.ts', use: { ...devices['Desktop Safari'] } },
    { name: 'mobile-chromium', testIgnore: '**/edge-csp-probe.spec.ts', use: { ...devices['Pixel 7'] } },
    // The existing real-Caddy Chromium probe runs once, outside the three application/device variants.
    { name: 'edge-csp-probe', testMatch: '**/edge-csp-probe.spec.ts' },
  ],
  webServer: {
    // Sem "pnpm" aqui: o pnpm vem do Corepack e pode não estar no PATH; o script e2e já põe node_modules/.bin no PATH.
    command: `vite build && vite preview --host 127.0.0.1 --port ${String(PREVIEW_PORT)} --strictPort`,
    url: BASE_URL,
    reuseExistingServer: !isCi,
    timeout: 120_000,
  },
})
