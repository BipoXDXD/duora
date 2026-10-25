// @vitest-environment node
import { describe, expect, it, vi } from 'vitest'
import { createApiClient, type ApiFetch } from './api/api-client'
import { createSessionContextStore } from './session-context'
import { MAX_AUDIO_INPUT_BYTES } from './pcm-wave'
import { createPrivateAudioCoordinator, loadOwnedAudio } from './private-audio'
import { fakeAudioContext, fakePcmBuffer } from '@/test/audio-context'
import { pcmWave } from '@/test/pcm-wave'

const PATH = '/api/v1/rooms/room/audio/cue'
function fixture(
  fetch: ApiFetch = () => Promise.resolve(new Response(pcmWave(), { headers: { 'Content-Type': 'audio/wav' } })),
  audio = fakeAudioContext(),
) {
  const store = createSessionContextStore()
  const owner = store.acceptBootstrap(store.beginBootstrap({ kind: 'automatic' }), {
    userId: '00000000-0000-4000-8000-000000000001',
    sessionId: '00000000-0000-4000-8000-000000000011',
    phase: 'active',
    requestToken: 'private-A',
    authView: { status: 'Active', emailConfirmed: true, mfaEnabled: false, roles: ['participant'] },
  })
  store.completeOwnerInstallation(store.getSnapshot().transitionEpoch, owner)
  const client = createApiClient({
    sessionContext: store,
    fetch,
    origin: 'https://duora.test',
    readLocale: () => 'pt-BR',
    readAntiforgeryToken: () => 'public-B',
    now: () => 0,
  })
  const createContext = vi.fn(() => audio.context)
  const coordinator = createPrivateAudioCoordinator({ client, createContext, origin: 'https://duora.test' })
  return { store, owner, audio, coordinator, createContext }
}

describe('um slot de áudio privado por página', () => {
  it('não cria contexto nem faz rede sem acquire do gesto; depois usa prova privada e começa mudo', async () => {
    const requests: Request[] = []
    const current = fixture((request) => {
      requests.push(request)
      return Promise.resolve(new Response(pcmWave(), { headers: { 'Content-Type': 'audio/wav' } }))
    })
    expect(current.createContext).not.toHaveBeenCalled()
    expect(requests).toEqual([])
    const operation = current.store.begin(current.owner)
    const lease = current.coordinator.acquire(operation)
    await loadOwnedAudio(operation, PATH, lease)
    await lease.play()
    expect(requests[0]?.headers.get('X-XSRF-TOKEN')).toBe('private-A')
    expect(requests[0]?.url).toBe(`https://duora.test${PATH}`)
    expect(current.audio.gain.gain.value).toBe(0)
    expect(current.audio.outputs[0]?.start).toHaveBeenCalledWith(0, 0)
    lease.setMuted(false)
    expect(current.audio.gain.gain.value).toBe(1)
    lease.pause()
    await lease.seek(0.05)
    await lease.play()
    expect(current.audio.outputs[1]?.start).toHaveBeenCalledWith(0, 0.05)
    lease.dispose()
    expect(current.audio.value.close).toHaveBeenCalledOnce()
  })

  it.each(['0', 'garbage', String(MAX_AUDIO_INPUT_BYTES + 1)])(
    'Content-Length inválido%s não chega ao decoder',
    async (length) => {
      const current = fixture(() =>
        Promise.resolve(
          new Response(pcmWave(), { headers: { 'Content-Type': 'audio/wav', 'Content-Length': length } }),
        ),
      )
      const operation = current.store.begin(current.owner)
      const lease = current.coordinator.acquire(operation)
      await expect(loadOwnedAudio(operation, PATH, lease)).rejects.toThrow('audio-format')
      expect(current.audio.value.decodeAudioData).not.toHaveBeenCalled()
    },
  )

  it('stream maior que6MiB é cancelado antes de decodificar', async () => {
    const cancel = vi.fn()
    let chunks = 0
    const stream = new ReadableStream<Uint8Array>({
      pull(controller) {
        chunks++
        controller.enqueue(new Uint8Array(4 * 1024 * 1024))
        if (chunks > 2) controller.close()
      },
      cancel,
    })
    const current = fixture(() => Promise.resolve(new Response(stream, { headers: { 'Content-Type': 'audio/wav' } })))
    const operation = current.store.begin(current.owner)
    await expect(loadOwnedAudio(operation, PATH, current.coordinator.acquire(operation))).rejects.toThrow(
      'audio-format',
    )
    expect(current.audio.value.decodeAudioData).not.toHaveBeenCalled()
    expect(cancel).toHaveBeenCalled()
  })

  it('sampleRate real acima48k falha antes de rede/decoder', () => {
    const current = fixture(undefined, fakeAudioContext({ sampleRate: 96_000 }))
    expect(() => current.coordinator.acquire(current.store.begin(current.owner))).toThrow('audio-unsupported')
    expect(current.audio.value.decodeAudioData).not.toHaveBeenCalled()
  })

  it('resultado inválido do decoder nunca inicia uma saída', async () => {
    const current = fixture(
      undefined,
      fakeAudioContext({ decode: () => Promise.resolve(fakePcmBuffer({ numberOfChannels: 3 })) }),
    )
    const operation = current.store.begin(current.owner)
    await expect(loadOwnedAudio(operation, PATH, current.coordinator.acquire(operation))).rejects.toThrow(
      'audio-format',
    )
    expect(current.audio.value.createBufferSource).not.toHaveBeenCalled()
  })

  it.each([
    { sampleRate: 8_000, frames: 64_000, targetRate: 48_000, decodedFrames: 383_995 },
    { sampleRate: 8_000, frames: 64_000, targetRate: 48_000, decodedFrames: 384_000 },
    { sampleRate: 44_100, frames: 352_800, targetRate: 48_000, decodedFrames: 383_999 },
    { sampleRate: 48_000, frames: 384_000, targetRate: 48_000, decodedFrames: 384_000 },
    { sampleRate: 48_000, frames: 384_000, targetRate: 8_000, decodedFrames: 64_000 },
  ])(
    'aceita PCM completo $sampleRate Hz/$frames frames convertido a$targetRate Hz/$decodedFrames frames',
    async ({ sampleRate, frames, targetRate, decodedFrames }) => {
      const current = fixture(
        () =>
          Promise.resolve(new Response(pcmWave({ sampleRate, frames }), { headers: { 'Content-Type': 'audio/wav' } })),
        fakeAudioContext({
          sampleRate: targetRate,
          decode: () =>
            Promise.resolve(
              fakePcmBuffer({
                sampleRate: targetRate,
                length: decodedFrames,
                duration: decodedFrames / targetRate,
              }),
            ),
        }),
      )
      const operation = current.store.begin(current.owner)
      const lease = current.coordinator.acquire(operation)
      await loadOwnedAudio(operation, PATH, lease)
      expect(lease.getSnapshot()).toMatchObject({ phase: 'ready', duration: decodedFrames / targetRate })
      await lease.play()
      expect(lease.getSnapshot().phase).toBe('playing')
      lease.dispose()
    },
  )

  it.each([
    { sampleRate: 8_000, frames: 64_000, targetRate: 48_000, decodedFrames: 383_994 },
    { sampleRate: 8_000, frames: 64_000, targetRate: 48_000, decodedFrames: 384_001 },
    { sampleRate: 44_100, frames: 352_800, targetRate: 48_000, decodedFrames: 383_998 },
    { sampleRate: 48_000, frames: 384_000, targetRate: 48_000, decodedFrames: 383_999 },
    { sampleRate: 48_000, frames: 384_000, targetRate: 48_000, decodedFrames: 384_001 },
    { sampleRate: 48_000, frames: 384_000, targetRate: 8_000, decodedFrames: 63_999 },
    { sampleRate: 48_000, frames: 384_000, targetRate: 8_000, decodedFrames: 64_001 },
  ])(
    'recusa PCM fora da janela $sampleRate Hz/$frames frames para$targetRate Hz/$decodedFrames frames',
    async ({ sampleRate, frames, targetRate, decodedFrames }) => {
      const current = fixture(
        () =>
          Promise.resolve(new Response(pcmWave({ sampleRate, frames }), { headers: { 'Content-Type': 'audio/wav' } })),
        fakeAudioContext({
          sampleRate: targetRate,
          decode: () =>
            Promise.resolve(
              fakePcmBuffer({
                sampleRate: targetRate,
                length: decodedFrames,
                duration: decodedFrames / targetRate,
              }),
            ),
        }),
      )
      const operation = current.store.begin(current.owner)
      const lease = current.coordinator.acquire(operation)
      await expect(loadOwnedAudio(operation, PATH, lease)).rejects.toThrow('audio-format')
      expect(lease.getSnapshot().phase).toBe('disposed')
      expect(current.audio.value.createBufferSource).not.toHaveBeenCalled()
    },
  )

  it('WAV truncado e recusa HTTP de prova privada não chegam ao decoder', async () => {
    for (const response of [
      new Response(pcmWave({ frames: 64_000 }).slice(0, -2), { headers: { 'Content-Type': 'audio/wav' } }),
      new Response(
        JSON.stringify({ type: 'about:blank', title: 'Forbidden', status: 403, code: 'CSRF_VALIDATION_FAILED' }),
        { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    ]) {
      const current = fixture(() => Promise.resolve(response))
      const operation = current.store.begin(current.owner)
      const lease = current.coordinator.acquire(operation)
      await expect(loadOwnedAudio(operation, PATH, lease)).rejects.toBeInstanceOf(Error)
      expect(lease.getSnapshot().phase).toBe('disposed')
      expect(current.audio.value.decodeAudioData).not.toHaveBeenCalled()
      expect(current.audio.value.createBufferSource).not.toHaveBeenCalled()
    }
  })

  it('decode não cancelável mantém o slot ocupado entre árvores até seu finally, sem som tardio', async () => {
    let resolve!: (value: AudioBuffer) => void
    let entered!: () => void
    const started = new Promise<void>((done) => {
      entered = done
    })
    const decoded = new Promise<AudioBuffer>((done) => {
      resolve = done
    })
    const current = fixture(
      undefined,
      fakeAudioContext({
        decode: () => {
          entered()
          return decoded
        },
      }),
    )
    const operation = current.store.begin(current.owner)
    const lease = current.coordinator.acquire(operation)
    const earlier = loadOwnedAudio(operation, PATH, lease).catch((error: unknown) => error as Error)
    await started
    lease.dispose()
    const nextOperation = current.store.begin(current.owner)
    expect(() => current.coordinator.acquire(nextOperation)).toThrow('audio-busy')
    expect(current.createContext).toHaveBeenCalledOnce()
    resolve(fakePcmBuffer())
    expect(await earlier).toMatchObject({ name: 'AbortError' })
    expect(current.audio.value.createBufferSource).not.toHaveBeenCalled()
    const next = current.coordinator.acquire(nextOperation)
    next.dispose()
  })

  it('proteger o contexto interrompe e desconecta a reprodução corrente', async () => {
    const current = fixture()
    const operation = current.store.begin(current.owner)
    const lease = current.coordinator.acquire(operation)
    await loadOwnedAudio(operation, PATH, lease)
    await lease.play()
    current.store.protect('identity')
    expect(current.audio.outputs[0]?.stop).toHaveBeenCalledOnce()
    expect(current.audio.outputs[0]?.disconnect).toHaveBeenCalledOnce()
    expect(current.audio.outputs[0]?.buffer).toBeNull()
    await expect(lease.play()).rejects.toMatchObject({ name: 'AbortError' })
  })
})
