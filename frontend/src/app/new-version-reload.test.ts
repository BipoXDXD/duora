import { describe, expect, it, vi } from 'vitest'
import { NEW_VERSION_RELOAD_KEY, NEW_VERSION_RELOAD_WINDOW_MS, reloadOnceForNewVersion } from './new-version-reload'

const NOW = 1_800_000_000_000

function memoryStorage(initial: Record<string, string> = {}): Pick<Storage, 'getItem' | 'setItem'> {
  const values = new Map(Object.entries(initial))
  return {
    getItem: (key) => values.get(key) ?? null,
    setItem: (key, value) => {
      values.set(key, value)
    },
  }
}

describe('reloadOnceForNewVersion (chunk de outra versão)', () => {
  it('sem recarga recente: recarrega e anota o instante', () => {
    const storage = memoryStorage()
    const reload = vi.fn()

    reloadOnceForNewVersion({ storage: () => storage, now: NOW, reload })

    expect(reload).toHaveBeenCalledTimes(1)
    expect(storage.getItem(NEW_VERSION_RELOAD_KEY)).toBe(String(NOW))
  })

  it('já recarregou há pouco: não recarrega de novo, para não entrar em laço', () => {
    const storage = memoryStorage({ [NEW_VERSION_RELOAD_KEY]: String(NOW - NEW_VERSION_RELOAD_WINDOW_MS + 1) })
    const reload = vi.fn()

    reloadOnceForNewVersion({ storage: () => storage, now: NOW, reload })

    expect(reload).not.toHaveBeenCalled()
  })

  it('a última recarga saiu da janela: um deploy novo na mesma aba recarrega outra vez', () => {
    const storage = memoryStorage({ [NEW_VERSION_RELOAD_KEY]: String(NOW - NEW_VERSION_RELOAD_WINDOW_MS) })
    const reload = vi.fn()

    reloadOnceForNewVersion({ storage: () => storage, now: NOW, reload })

    expect(reload).toHaveBeenCalledTimes(1)
  })

  it.each(['abc', '', String(NOW + 5_000)])(
    'valor guardado inválido ou no futuro (%j) não trava a recarga',
    (stored) => {
      const reload = vi.fn()

      reloadOnceForNewVersion({
        storage: () => memoryStorage({ [NEW_VERSION_RELOAD_KEY]: stored }),
        now: NOW,
        reload,
      })

      expect(reload).toHaveBeenCalledTimes(1)
    },
  )

  it('sem armazenamento da sessão não recarrega: sem a guarda, recarregar poderia não parar', () => {
    const reload = vi.fn()
    const storage = {
      getItem: () => {
        throw new DOMException('bloqueado', 'SecurityError')
      },
      setItem: () => undefined,
    }

    reloadOnceForNewVersion({ storage: () => storage, now: NOW, reload })

    expect(reload).not.toHaveBeenCalled()
  })

  it('não consegue gravar a guarda: não recarrega', () => {
    const reload = vi.fn()
    const storage = {
      getItem: () => null,
      setItem: () => {
        throw new DOMException('cheio', 'QuotaExceededError')
      },
    }

    reloadOnceForNewVersion({ storage: () => storage, now: NOW, reload })

    expect(reload).not.toHaveBeenCalled()
  })

  it('o próprio acesso ao sessionStorage lança: não recarrega', () => {
    const reload = vi.fn()

    reloadOnceForNewVersion({
      storage: () => {
        throw new DOMException('bloqueado', 'SecurityError')
      },
      now: NOW,
      reload,
    })

    expect(reload).not.toHaveBeenCalled()
  })
})
