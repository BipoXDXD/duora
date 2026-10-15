import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterAll, afterEach, beforeAll } from 'vitest'
import { clearAntiforgeryCookie, server } from './msw'

// Requisição sem handler é erro: nenhum teste fala com a rede de verdade.
beforeAll(() => {
  server.listen({ onUnhandledFrame: 'error' })
})

afterEach(() => {
  cleanup()
  server.resetHandlers()
  if (typeof document !== 'undefined') {
    clearAntiforgeryCookie()
  }
})

afterAll(() => {
  server.close()
})
