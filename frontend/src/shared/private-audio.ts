import type { ApiClient } from './api/api-client'
import { contextAbort, type OperationHandle } from './session-context'
import { AUDIO_RESERVED_BYTES, inspectCanonicalPcm, MAX_AUDIO_INPUT_BYTES, MAX_AUDIO_PCM_BYTES } from './pcm-wave'

export interface AudioSnapshot {
  readonly phase: 'empty' | 'loading' | 'ready' | 'playing' | 'paused' | 'ended' | 'disposed'
  readonly duration: number
  readonly muted: boolean
}
export interface AudioLease {
  load(operation: OperationHandle, path: string): Promise<void>
  play(): Promise<void>
  pause(): void
  seek(position: number): Promise<void>
  setMuted(muted: boolean): void
  position(): number
  dispose(): void
  subscribe(listener: () => void): () => void
  getSnapshot(): AudioSnapshot
}
export interface PrivateAudioCoordinator {
  acquire(operation: OperationHandle): AudioLease
}
interface AudioDependencies {
  readonly client: ApiClient
  readonly origin: string
  readonly createContext: () => AudioContext
}

/** Um slot por documento. Mesmo descartado, fetch/decode pendente segura o slot até o finally. */
export function createPrivateAudioCoordinator(dependencies: AudioDependencies): PrivateAudioCoordinator {
  if (AUDIO_RESERVED_BYTES > 72 * 1024 * 1024) throw new Error('audio-budget')
  let active: { readonly lease: AudioLease; readonly busy: () => boolean } | null = null
  return {
    acquire(operation) {
      const store = dependencies.client.sessionContext
      store.assertCurrent(operation)
      if (active !== null) {
        if (active.busy()) throw new Error('audio-busy')
        active.lease.dispose()
      }
      const context = dependencies.createContext()
      if (context.sampleRate < 8_000 || context.sampleRate > 48_000) {
        void context.close().catch(() => undefined)
        throw new Error('audio-unsupported')
      }
      // Resume é chamado no gesto, antes de qualquer await/fetch, para respeitar a ativação do navegador.
      const resumed = context.resume()
      void resumed.catch(() => undefined)
      const gain = context.createGain()
      gain.gain.value = 0
      gain.connect(context.destination)
      let buffer: AudioBuffer | null = null
      let output: AudioBufferSourceNode | null = null
      let loading = false
      let started = false
      let disposed = false
      let offset = 0
      let beganAt = 0
      let playVersion = 0
      let snapshot: AudioSnapshot = Object.freeze({ phase: 'empty', duration: 0, muted: true })
      const listeners = new Set<() => void>()
      let unsubscribe = (): void => undefined
      const publish = (phase: AudioSnapshot['phase']): void => {
        snapshot = Object.freeze({ ...snapshot, phase })
        for (const listener of [...listeners]) listener()
      }
      const assertCurrent = (): void => {
        if (disposed) throw contextAbort()
        store.assertCurrent(operation)
      }
      const position = (): number =>
        Math.min(snapshot.duration, output === null ? offset : offset + context.currentTime - beganAt)
      const stopOutput = (): void => {
        const previous = output
        output = null
        if (previous !== null) {
          previous.onended = null
          try {
            previous.stop()
          } catch {
            /* Já encerrado pelo dispositivo. */
          }
          previous.disconnect()
          previous.buffer = null
        }
      }
      const releaseSlot = (): void => {
        if (active?.lease === lease && disposed && !loading) active = null
      }
      const lease: AudioLease = {
        async load(source, path) {
          assertCurrent()
          if (source !== operation) throw contextAbort()
          if (started) throw new Error('audio-busy')
          if (!/^\/api\/v1\/rooms\/[^/?#]+\/audio\/[^/?#]+$/.test(path)) throw new Error('audio-path')
          started = true
          loading = true
          publish('loading')
          try {
            await resumed
            assertCurrent()
            const response = await dependencies.client.fetchFor(operation)(
              new Request(new URL(path, dependencies.origin)),
            )
            assertCurrent()
            const encoded = await readBoundedWave(response, assertCurrent)
            assertCurrent()
            const description = inspectCanonicalPcm(encoded)
            const decoded = await context.decodeAudioData(encoded.buffer)
            assertCurrent()
            if (
              decoded.numberOfChannels !== description.channels ||
              decoded.sampleRate !== context.sampleRate ||
              decoded.length < 1 ||
              decoded.length * decoded.numberOfChannels * 4 > MAX_AUDIO_PCM_BYTES ||
              !Number.isFinite(decoded.duration) ||
              decoded.duration > 60 ||
              Math.abs(decoded.duration - description.duration) > 1 / context.sampleRate
            )
              throw new Error('audio-format')
            buffer = decoded
            snapshot = Object.freeze({ ...snapshot, duration: decoded.duration })
            publish('ready')
          } catch (error) {
            if (disposed) throw contextAbort()
            lease.dispose()
            throw error
          } finally {
            loading = false
            releaseSlot()
          }
        },
        async play() {
          assertCurrent()
          if (buffer === null) throw new Error('audio-not-ready')
          const version = ++playVersion
          await context.resume()
          assertCurrent()
          if (version !== playVersion) throw contextAbort()
          if (output !== null) return
          if (offset >= snapshot.duration) offset = 0
          const source = context.createBufferSource()
          source.buffer = buffer
          source.connect(gain)
          source.onended = () => {
            if (disposed || output !== source) return
            offset = snapshot.duration
            stopOutput()
            publish('ended')
          }
          output = source
          beganAt = context.currentTime
          source.start(0, offset)
          publish('playing')
        },
        pause() {
          assertCurrent()
          playVersion += 1
          offset = position()
          stopOutput()
          publish('paused')
        },
        async seek(value) {
          assertCurrent()
          if (!Number.isFinite(value) || buffer === null) throw new Error('audio-position')
          const playing = output !== null
          lease.pause()
          offset = Math.max(0, Math.min(value, snapshot.duration))
          if (playing && offset < snapshot.duration) await lease.play()
          else if (offset >= snapshot.duration) publish('ended')
        },
        setMuted(muted) {
          assertCurrent()
          gain.gain.value = muted ? 0 : 1
          snapshot = Object.freeze({ ...snapshot, muted })
          publish(snapshot.phase)
        },
        position,
        dispose() {
          if (disposed) return
          disposed = true
          playVersion += 1
          unsubscribe()
          stopOutput()
          buffer = null
          offset = 0
          gain.disconnect()
          void context.close().catch(() => undefined)
          store.finish(operation)
          publish('disposed')
          listeners.clear()
          releaseSlot()
        },
        subscribe(listener) {
          listeners.add(listener)
          return () => {
            listeners.delete(listener)
          }
        },
        getSnapshot: () => snapshot,
      }
      active = { lease, busy: () => loading }
      unsubscribe = store.subscribe(() => {
        try {
          assertCurrent()
        } catch {
          lease.dispose()
        }
      })
      return lease
    },
  }
}

async function readBoundedWave(response: Response, assertCurrent: () => void): Promise<Uint8Array<ArrayBuffer>> {
  const declared = response.headers.get('Content-Length')
  const type = response.headers.get('Content-Type')?.split(';')[0]?.trim().toLowerCase()
  if (
    type !== 'audio/wav' ||
    (declared !== null &&
      (!/^\d+$/.test(declared) || Number(declared) < 46 || Number(declared) > MAX_AUDIO_INPUT_BYTES))
  ) {
    await response.body?.cancel()
    throw new Error('audio-format')
  }
  if (response.body === null) throw new Error('audio-format')
  const reader = response.body.getReader()
  const collection = new Uint8Array(MAX_AUDIO_INPUT_BYTES)
  let offset = 0
  try {
    for (;;) {
      assertCurrent()
      const chunk = await reader.read()
      assertCurrent()
      if (chunk.done) break
      if (chunk.value.byteLength > MAX_AUDIO_INPUT_BYTES - offset) throw new Error('audio-format')
      collection.set(chunk.value, offset)
      offset += chunk.value.byteLength
    }
    if (declared !== null && Number(declared) !== offset) throw new Error('audio-format')
    const encoded = collection.slice(0, offset)
    return encoded
  } finally {
    await reader.cancel().catch(() => undefined)
    reader.releaseLock()
  }
}

export function loadOwnedAudio(operation: OperationHandle, path: string, lease: AudioLease): Promise<void> {
  return lease.load(operation, path)
}
