export const MAX_AUDIO_INPUT_BYTES = 6 * 1024 * 1024
export const MAX_AUDIO_PCM_BYTES = 48_000 * 60 * 2 * 4
export const AUDIO_RESERVED_BYTES = 4 * MAX_AUDIO_INPUT_BYTES + 2 * MAX_AUDIO_PCM_BYTES
export interface PcmDescription {
  readonly channels: number
  readonly sampleRate: number
  readonly frames: number
  readonly duration: number
}
export function inspectCanonicalPcm(bytes: Uint8Array): PcmDescription {
  const fail = (): never => {
    throw new Error('audio-format')
  }
  if (bytes.byteLength < 46 || bytes.byteLength > MAX_AUDIO_INPUT_BYTES) fail()
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
  if (
    view.getUint32(0, true) !== 0x46464952 ||
    view.getUint32(8, true) !== 0x45564157 ||
    view.getUint32(12, true) !== 0x20746d66 ||
    view.getUint32(36, true) !== 0x61746164 ||
    view.getUint32(4, true) !== bytes.byteLength - 8 ||
    view.getUint32(16, true) !== 16 ||
    view.getUint16(20, true) !== 1 ||
    view.getUint16(34, true) !== 16 ||
    view.getUint32(40, true) !== bytes.byteLength - 44
  )
    fail()
  const channels = view.getUint16(22, true)
  const sampleRate = view.getUint32(24, true)
  const alignment = view.getUint16(32, true)
  if (
    channels < 1 ||
    channels > 2 ||
    sampleRate < 8_000 ||
    sampleRate > 48_000 ||
    alignment !== channels * 2 ||
    view.getUint32(28, true) !== sampleRate * alignment ||
    (bytes.byteLength - 44) % alignment !== 0 ||
    bytes.byteLength - 44 > sampleRate * alignment * 60
  )
    fail()
  const frames = (bytes.byteLength - 44) / alignment
  return { channels, sampleRate, frames, duration: frames / sampleRate }
}
