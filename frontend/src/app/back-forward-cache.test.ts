import { describe, expect, it, vi } from 'vitest'
import { isPrivatePath, reloadWhenRestoredFromBackForwardCache } from './back-forward-cache'

function setup({ privatePage = true } = {}) {
  const target = new EventTarget()
  const hide = vi.fn()
  const reload = vi.fn()
  const stop = reloadWhenRestoredFromBackForwardCache({ target, isPrivatePage: () => privatePage, hide, reload })
  return { target, hide, reload, stop }
}

const pageTransition = (type: 'pagehide' | 'pageshow', persisted: boolean) =>
  new PageTransitionEvent(type, { persisted })

describe('reloadWhenRestoredFromBackForwardCache', () => {
  it('página privada restaurada do bfcache recarrega, para a sessão ser conferida no servidor (T-21)', () => {
    const { target, reload } = setup()

    target.dispatchEvent(pageTransition('pageshow', true))

    expect(reload).toHaveBeenCalledOnce()
  })

  it('carga normal da página não recarrega', () => {
    const { target, reload } = setup()

    target.dispatchEvent(pageTransition('pageshow', false))

    expect(reload).not.toHaveBeenCalled()
  })

  it('página privada que entra no bfcache é escondida, para não reaparecer com dados antes da recarga', () => {
    const { target, hide } = setup()

    target.dispatchEvent(pageTransition('pagehide', true))

    expect(hide).toHaveBeenCalledOnce()
  })

  it('página descartada ao sair não precisa ser escondida', () => {
    const { target, hide } = setup()

    target.dispatchEvent(pageTransition('pagehide', false))

    expect(hide).not.toHaveBeenCalled()
  })

  it('página pública volta do bfcache como estava, sem perder o rascunho de um formulário', () => {
    const { target, hide, reload } = setup({ privatePage: false })

    target.dispatchEvent(pageTransition('pagehide', true))
    target.dispatchEvent(pageTransition('pageshow', true))

    expect(hide).not.toHaveBeenCalled()
    expect(reload).not.toHaveBeenCalled()
  })

  it('depois de parar, nada reage', () => {
    const { target, hide, reload, stop } = setup()
    stop()

    target.dispatchEvent(pageTransition('pagehide', true))
    target.dispatchEvent(pageTransition('pageshow', true))

    expect(hide).not.toHaveBeenCalled()
    expect(reload).not.toHaveBeenCalled()
  })
})

describe('isPrivatePath', () => {
  it.each(['/', '/register', '/check-email', '/confirm-email', '/login', '/forgot-password', '/reset-password'])(
    '%s é pública',
    (pathname) => {
      expect(isPrivatePath(pathname)).toBe(false)
    },
  )

  it.each(['/account', '/perfil', '/rota-nova', '/login/', '/ENTRAR'])(
    '%s é privada (negar por padrão)',
    (pathname) => {
      expect(isPrivatePath(pathname)).toBe(true)
    },
  )
})
