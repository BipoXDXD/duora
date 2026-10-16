import { paths } from '@/shared/paths'

/**
 * Rotas que nunca mostram dado privado. Qualquer outra conta como privada (negar por padrão): uma tela nova da área
 * autenticada já nasce protegida.
 */
const PUBLIC_PATHS: ReadonlySet<string> = new Set([
  paths.home,
  paths.register,
  paths.checkEmail,
  paths.confirmEmail,
  paths.login,
  paths.forgotPassword,
  paths.resetPassword,
])

export function isPrivatePath(pathname: string): boolean {
  return !PUBLIC_PATHS.has(pathname)
}

interface BackForwardCacheGuard {
  /** Normalmente `window`; recebe `pagehide` e `pageshow`. */
  readonly target: EventTarget
  /** Se a página atual pode mostrar dado privado; nas públicas o bfcache segue normal. */
  readonly isPrivatePage: () => boolean
  /** Tira a página de vista antes de ela ir para o bfcache. */
  readonly hide: () => void
  readonly reload: () => void
}

/**
 * O bfcache devolve a página inteira, com DOM e memória, ao voltar no navegador, sem perguntar nada ao servidor:
 * se a sessão expirou ou outra pessoa entrou nesse meio-tempo, a tela privada de antes voltaria com os dados de
 * quem a deixou (T-21, T-14). Numa página privada, ela é escondida ao entrar no bfcache e, se for restaurada,
 * recarrega; a recarga consulta a identidade no servidor. Páginas públicas ficam de fora, para o rascunho de um
 * cadastro sobreviver a uma ida e volta. Devolve a função que remove os listeners.
 */
export function reloadWhenRestoredFromBackForwardCache({
  target,
  isPrivatePage,
  hide,
  reload,
}: BackForwardCacheGuard): () => void {
  const onPageHide = (event: Event): void => {
    if (isPersisted(event) && isPrivatePage()) {
      hide()
    }
  }
  const onPageShow = (event: Event): void => {
    if (isPersisted(event) && isPrivatePage()) {
      reload()
    }
  }
  target.addEventListener('pagehide', onPageHide)
  target.addEventListener('pageshow', onPageShow)
  return () => {
    target.removeEventListener('pagehide', onPageHide)
    target.removeEventListener('pageshow', onPageShow)
  }
}

function isPersisted(event: Event): boolean {
  return event instanceof PageTransitionEvent && event.persisted
}
