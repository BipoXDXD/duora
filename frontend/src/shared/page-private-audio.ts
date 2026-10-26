import { apiClient } from './api'
import { createPrivateAudioCoordinator } from './private-audio'

/** Fora das árvores React e das épocas: um decoder antigo não perde sua reserva ao desmontar. */
export const pagePrivateAudio = createPrivateAudioCoordinator({
  client: apiClient,
  origin: globalThis.location.origin,
  createContext: () => {
    if (typeof AudioContext === 'undefined') throw new Error('audio-unsupported')
    return new AudioContext({ sampleRate: 48_000 })
  },
})
