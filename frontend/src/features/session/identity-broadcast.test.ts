// @vitest-environment node
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createIdentityBroadcast } from './identity-broadcast'

const unsubscribers: (() => void)[] = []

function listen(tabId: string): ReturnType<typeof vi.fn> {
  const listener = vi.fn()
  unsubscribers.push(createIdentityBroadcast(() => tabId).subscribe(listener))
  return listener
}

/** BroadcastChannel entrega de forma assíncrona; uma volta no laço de eventos basta entre objetos do mesmo processo. */
function deliveries(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 20))
}

afterEach(() => {
  unsubscribers.splice(0).forEach((unsubscribe) => {
    unsubscribe()
  })
})

describe('createIdentityBroadcast', () => {
  it('avisa as outras abas quando a identidade muda', async () => {
    const otherTab = listen('aba-b')

    createIdentityBroadcast(() => 'aba-a').announce()
    await deliveries()

    expect(otherTab).toHaveBeenCalledOnce()
  })

  it('a própria aba não recebe o próprio aviso', async () => {
    const sameTab = listen('aba-a')

    createIdentityBroadcast(() => 'aba-a').announce()
    await deliveries()

    expect(sameTab).not.toHaveBeenCalled()
  })

  it('depois de cancelar a inscrição, nada chega', async () => {
    const listener = vi.fn()
    const unsubscribe = createIdentityBroadcast(() => 'aba-b').subscribe(listener)
    unsubscribe()

    createIdentityBroadcast(() => 'aba-a').announce()
    await deliveries()

    expect(listener).not.toHaveBeenCalled()
  })

  it.each([
    ['texto solto', 'identity-changed'],
    ['tipo desconhecido', { type: 'outra-coisa', tabId: 'aba-a' }],
    ['sem aba de origem', { type: 'identity-changed' }],
    ['nulo', null],
  ])('ignora mensagem fora do formato: %s', async (_caso, message) => {
    const listener = listen('aba-b')

    const channel = new BroadcastChannel('duora.identity')
    channel.postMessage(message)
    channel.close()
    await deliveries()

    expect(listener).not.toHaveBeenCalled()
  })

  it('sem BroadcastChannel, o id da aba nem chega a ser gerado', () => {
    vi.stubGlobal('BroadcastChannel', undefined)
    const tabId = vi.fn(() => 'aba-a')
    try {
      const broadcast = createIdentityBroadcast(tabId)
      broadcast.subscribe(vi.fn())()
      broadcast.announce()
    } finally {
      vi.unstubAllGlobals()
    }

    expect(tabId).not.toHaveBeenCalled()
  })

  it('sem BroadcastChannel no navegador, anunciar e assinar não falham', () => {
    vi.stubGlobal('BroadcastChannel', undefined)
    try {
      const broadcast = createIdentityBroadcast(() => 'aba-a')
      expect(() => {
        broadcast.subscribe(vi.fn())()
        broadcast.announce()
      }).not.toThrow()
    } finally {
      vi.unstubAllGlobals()
    }
  })
})
