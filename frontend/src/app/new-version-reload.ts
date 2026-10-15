/** Chave em `sessionStorage` com o instante (ms desde a época) da última recarga por versão nova. */
export const NEW_VERSION_RELOAD_KEY = 'duora.newVersionReloadAt'

/** Dentro desta janela não se recarrega de novo: se a versão nova também falhar, quem decide é a pessoa. */
export const NEW_VERSION_RELOAD_WINDOW_MS = 60_000

interface ReloadOnceInput {
  /** Função, e não o objeto: em alguns navegadores ler `window.sessionStorage` já lança quando ele está bloqueado. */
  readonly storage: () => Pick<Storage, 'getItem' | 'setItem'>
  readonly now: number
  readonly reload: () => void
}

/**
 * Chunk que não baixa costuma ser deploy novo com a aba aberta: recarregar busca a versão publicada.
 * Recarrega uma vez por janela, com a guarda na sessão da aba. Sem conseguir ler ou gravar a guarda, não
 * recarrega, porque não haveria como parar; o error boundary da área oferece recarregar à mão.
 */
export function reloadOnceForNewVersion({ storage, now, reload }: ReloadOnceInput): void {
  try {
    const session = storage()
    const lastReloadAt = Number(session.getItem(NEW_VERSION_RELOAD_KEY) ?? Number.NaN)
    const reloadedRecently = lastReloadAt <= now && now - lastReloadAt < NEW_VERSION_RELOAD_WINDOW_MS
    if (reloadedRecently) {
      return
    }
    session.setItem(NEW_VERSION_RELOAD_KEY, String(now))
  } catch {
    // Armazenamento bloqueado ou cheio: sem guarda não há recarga automática segura (veja acima).
    return
  }
  reload()
}
