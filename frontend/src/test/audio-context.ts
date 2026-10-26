import { vi } from 'vitest'

function buildAudioContext(
  options: { readonly sampleRate?: number; readonly decode?: () => Promise<AudioBuffer> } = {},
) {
  const outputs: {
    buffer: AudioBuffer | null
    onended: (() => void) | null
    start: ReturnType<typeof vi.fn>
    stop: ReturnType<typeof vi.fn>
    disconnect: ReturnType<typeof vi.fn>
    connect: ReturnType<typeof vi.fn>
  }[] = []
  const gain = { gain: { value: 1 }, connect: vi.fn(), disconnect: vi.fn() }
  const value = {
    sampleRate: options.sampleRate ?? 48_000,
    currentTime: 0,
    destination: {},
    resume: vi.fn(() => Promise.resolve()),
    close: vi.fn(() => Promise.resolve()),
    decodeAudioData: vi.fn(options.decode ?? (() => Promise.resolve(fakePcmBuffer()))),
    createGain: vi.fn(() => gain),
    createBufferSource: vi.fn(() => {
      const output = {
        buffer: null,
        onended: null,
        start: vi.fn(),
        stop: vi.fn(),
        disconnect: vi.fn(),
        connect: vi.fn(),
      }
      outputs.push(output)
      return output
    }),
  }
  return { context: value as unknown as AudioContext, value, gain, outputs }
}

export function fakePcmBuffer(overrides: Partial<AudioBuffer> = {}): AudioBuffer {
  const buffer: AudioBuffer = {
    duration: 0.1,
    length: 4800,
    sampleRate: 48_000,
    numberOfChannels: 1,
    getChannelData: () => new Float32Array(4800),
    copyFromChannel: () => undefined,
    copyToChannel: () => undefined,
    ...overrides,
  }
  return buffer
}

export function fakeAudioContext(
  options: Parameters<typeof buildAudioContext>[0] = {},
): ReturnType<typeof buildAudioContext> {
  return buildAudioContext(options)
}
