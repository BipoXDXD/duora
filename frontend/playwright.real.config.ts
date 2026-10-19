import { defineConfig, devices } from '@playwright/test'

export default defineConfig({
  testDir: './e2e-real',
  workers: 1,
  fullyParallel: false,
  timeout: 120_000,
  expect: { timeout: 15_000 },
  use: { baseURL: 'https://localhost:5443', ignoreHTTPSErrors: true, locale: 'en-US', trace: 'retain-on-failure' },
  projects: [{ name: 'real-chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'corepack pnpm build && dotnet run --project ../backend/tests/Duora.BrowserHost',
    url: 'https://localhost:5443/__test/seed',
    ignoreHTTPSErrors: true,
    reuseExistingServer: false,
    timeout: 180_000,
    gracefulShutdown: { signal: 'SIGTERM', timeout: 15_000 },
  },
})
