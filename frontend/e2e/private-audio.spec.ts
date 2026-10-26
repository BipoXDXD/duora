import { readFileSync } from 'node:fs'
import { expect, test } from '@playwright/test'

function canonicalWave(): Buffer {
  const frames = 8_000 * 8
  const bytes = Buffer.alloc(44 + frames * 2)
  bytes.write('RIFF', 0)
  bytes.writeUInt32LE(bytes.length - 8, 4)
  bytes.write('WAVEfmt ', 8)
  bytes.writeUInt32LE(16, 16)
  bytes.writeUInt16LE(1, 20)
  bytes.writeUInt16LE(1, 22)
  bytes.writeUInt32LE(8000, 24)
  bytes.writeUInt32LE(16000, 28)
  bytes.writeUInt16LE(2, 32)
  bytes.writeUInt16LE(16, 34)
  bytes.write('data', 36)
  bytes.writeUInt32LE(frames * 2, 40)
  return bytes
}

test('player privado: gesto, teclado e PCM real com a CSP literal da borda', async ({ page, baseURL }) => {
  await page.addInitScript(() => {
    if (typeof AudioContext === 'undefined') return
    class ObservedAudioContext extends AudioContext {
      override async decodeAudioData(data: ArrayBuffer): Promise<AudioBuffer> {
        const inputBytes = data.byteLength
        try {
          const decoded = await super.decodeAudioData(data)
          Reflect.set(window, '__syntheticPcmDecode', {
            inputBytes,
            contextRate: this.sampleRate,
            decodedRate: decoded.sampleRate,
            channels: decoded.numberOfChannels,
            frames: decoded.length,
            duration: decoded.duration,
          })
          return decoded
        } catch (error) {
          Reflect.set(window, '__syntheticPcmDecode', {
            inputBytes,
            contextRate: this.sampleRate,
            error: error instanceof Error ? error.name : 'unknown',
          })
          throw error
        }
      }
    }
    Reflect.set(window, 'AudioContext', ObservedAudioContext)
  })
  const caddyfile = readFileSync(new URL('../../infra/caddy/Caddyfile', import.meta.url), 'utf8')
  const csp = /^\s*Content-Security-Policy "([^"]+)"$/m.exec(caddyfile)?.[1]
  expect(csp).toBeDefined()
  const host = new URL(baseURL ?? 'http://127.0.0.1:4173').host
  await page.route('**/rooms/audio-room', async (route) => {
    if (route.request().resourceType() !== 'document') {
      await route.fallback()
      return
    }
    const response = await route.fetch()
    await route.fulfill({
      response,
      headers: { ...response.headers(), 'content-security-policy': (csp ?? '').replaceAll('{hostport}', host) },
    })
  })
  let releaseAudio: (() => void) | undefined
  const audioReady = new Promise<void>((resolve) => {
    releaseAudio = resolve
  })
  let reads = 0
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/v1/me') {
      await route.fulfill({
        json: {
          id: '00000000-0000-4000-8000-000000000001',
          email: 'synthetic@example.test',
          status: 'Active',
          emailConfirmed: true,
          mfaEnabled: false,
          roles: ['participant'],
          sessionContext: {
            userId: '00000000-0000-4000-8000-000000000001',
            sessionId: '00000000-0000-4000-8000-000000000011',
            phase: 'active',
            requestToken: 'private-audio-A',
          },
        },
      })
      return
    }
    expect(route.request().headers()['x-xsrf-token']).toBe('private-audio-A')
    if (path === '/api/v1/rooms/audio-room') {
      await route.fulfill({
        json: {
          roomId: 'audio-room',
          state: 'Active',
          version: '1',
          communicationLanguage: 'pt',
          communicationPolicyVersion: 1,
          displayLocale: 'pt-BR',
          invitationExpiresAt: '2026-10-24T10:00:00Z',
          game: null,
          audio: [
            {
              cueId: 'cue',
              assetId: 'synthetic',
              transcript: 'Transcrição sempre disponível',
              accessibleDescription: 'Áudio sintético',
              sha256: 'a'.repeat(64),
              url: '/api/v1/rooms/audio-room/audio/cue',
            },
          ],
        },
      })
      return
    }
    if (path === '/api/v1/rooms/audio-room/audio/cue') {
      reads += 1
      await audioReady
      await route.fulfill({ body: canonicalWave(), contentType: 'audio/wav', headers: { 'Cache-Control': 'no-store' } })
      return
    }
    await route.fulfill({ status: 404 })
  })
  await page.goto('/rooms/audio-room')
  await expect(page.getByText('Transcrição sempre disponível')).toBeVisible()
  expect(reads).toBe(0)
  await expect(page.locator('audio,source')).toHaveCount(0)
  const play = page.getByRole('button', { name: 'Reproduzir áudio', exact: true })
  await play.focus()
  await page.keyboard.press('Enter')
  await expect(page.getByText('Preparando áudio…', { exact: true })).toBeVisible()
  await expect(play).toBeFocused()
  await expect(play).toHaveAttribute('aria-disabled', 'true')
  releaseAudio?.()
  const pause = page.getByRole('button', { name: 'Pausar áudio', exact: true })
  try {
    await expect(pause).toBeFocused()
  } catch (error) {
    const diagnostic: unknown = await page.evaluate(() => {
      const value: unknown = Reflect.get(window, '__syntheticPcmDecode')
      return value
    })
    console.info('Synthetic PCM decoder metadata:', JSON.stringify(diagnostic))
    throw error
  }
  const sound = page.getByRole('button', { name: 'Ativar som', exact: true })
  await expect(sound).toHaveAttribute('aria-pressed', 'false')
  await sound.click()
  await expect(page.getByRole('button', { name: 'Silenciar áudio', exact: true })).toHaveAttribute(
    'aria-pressed',
    'true',
  )
  const seek = page.getByRole('slider', { name: 'Posição do áudio', exact: true })
  await seek.focus()
  await page.keyboard.press('Home')
  await page.keyboard.press('ArrowRight')
  await expect(seek).toBeFocused()
  await expect.poll(() => seek.inputValue()).not.toBe('0')
  await pause.focus()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('button', { name: 'Reproduzir áudio', exact: true })).toBeFocused()
  await page.getByRole('button', { name: 'Reiniciar áudio', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Pausar áudio', exact: true })).toBeVisible()
  expect(reads).toBe(1)
})
