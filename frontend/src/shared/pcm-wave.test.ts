// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { AUDIO_RESERVED_BYTES, inspectCanonicalPcm, MAX_AUDIO_INPUT_BYTES } from './pcm-wave'

import { pcmWave } from '@/test/pcm-wave'

describe('PCM canônico com os mesmos limites do catálogo', () => {
  it('interpreta PCM16 mono e stereo com limite de representações menor que72MiB', () => {
    expect(inspectCanonicalPcm(pcmWave())).toEqual({ channels: 1, sampleRate: 8000, frames: 800, duration: 0.1 })
    expect(inspectCanonicalPcm(pcmWave({ channels: 2, sampleRate: 48000, frames: 48_000 }))).toMatchObject({
      channels: 2,
      duration: 1,
    })
    expect(AUDIO_RESERVED_BYTES).toBe(71_245_824)
    expect(AUDIO_RESERVED_BYTES).toBeLessThan(72 * 1024 * 1024)
  })
  it.each([
    ['mais que60s', () => pcmWave({ frames: 8_000 * 60 + 1 })],
    ['mais que2canais', () => pcmWave({ channels: 3 })],
    ['sampleRate acima48k', () => pcmWave({ sampleRate: 48_001 })],
    ['sampleRate abaixo8k', () => pcmWave({ sampleRate: 7_999 })],
    ['sem amostras', () => pcmWave({ frames: 0 })],
    ['input acima6MiB', () => new Uint8Array(MAX_AUDIO_INPUT_BYTES + 1)],
  ])('recusa %s antes de qualquer decodificação', (_name, build) => {
    expect(() => inspectCanonicalPcm(build())).toThrow('audio-format')
  })
  it.each([0, 4, 8, 12, 16, 20, 28, 32, 34, 36, 40])('recusa campo canônico adulterado no offset%i', (offset) => {
    const bytes = pcmWave()
    bytes[offset] = (bytes[offset] ?? 0) ^ 1
    expect(() => inspectCanonicalPcm(bytes)).toThrow('audio-format')
  })
})
