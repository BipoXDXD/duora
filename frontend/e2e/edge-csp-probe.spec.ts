import { execFile } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { promisify } from 'node:util'
import { expect, test } from '@playwright/test'

const execute = promisify(execFile)

test('Caddy real: áudio privado e rejeição de fontes Blob/data no Chromium', async () => {
  test.setTimeout(330_000)
  const { stdout, stderr } = await execute(
    'python3',
    [
      '-m',
      'unittest',
      'test_caddy_security_headers.CaddySecurityHeadersTests.test_browser_enforces_the_policies',
      '-v',
    ],
    {
      cwd: fileURLToPath(new URL('../../scripts/deploy/', import.meta.url)),
      timeout: 300_000,
      maxBuffer: 4 * 1024 * 1024,
      env: {
        PATH: process.env.PATH ?? '',
        HOME: process.env.HOME ?? '',
        NODE_OPTIONS: '--max-old-space-size=512',
        DUORA_TEST_BROWSER: '1',
        ...(process.env.PLAYWRIGHT_BROWSERS_PATH === undefined
          ? {}
          : { PLAYWRIGHT_BROWSERS_PATH: process.env.PLAYWRIGHT_BROWSERS_PATH }),
      },
    },
  )
  expect(stderr).toMatch(/Ran 1 test/)
  expect(stderr).not.toMatch(/skipped/i)
  expect(stdout).toContain('PASS private audio refuses a request without the owned proof')
  expect(stdout).toContain('PASS private PCM decodes through owned fetch under the unchanged CSP')
  expect(stdout).toContain('PASS data media blocked by CSP')
  expect(stdout).toContain('PASS blob media blocked by CSP')
})
