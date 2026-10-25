export function pcmWave({ channels = 1, sampleRate = 8_000, frames = 800 } = {}): Uint8Array<ArrayBuffer> {
  const data = frames * channels * 2
  const bytes = new Uint8Array(44 + data)
  const view = new DataView(bytes.buffer)
  const text = (offset: number, value: string) => {
    bytes.set(new TextEncoder().encode(value), offset)
  }
  text(0, 'RIFF')
  view.setUint32(4, bytes.length - 8, true)
  text(8, 'WAVEfmt ')
  view.setUint32(16, 16, true)
  view.setUint16(20, 1, true)
  view.setUint16(22, channels, true)
  view.setUint32(24, sampleRate, true)
  view.setUint32(28, sampleRate * channels * 2, true)
  view.setUint16(32, channels * 2, true)
  view.setUint16(34, 16, true)
  text(36, 'data')
  view.setUint32(40, data, true)
  return bytes
}
