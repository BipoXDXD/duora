import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useId, useRef } from 'react'
import { useLocation } from 'react-router'
import { useAccount } from '@/features/session/account-context'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { Button } from '@/shared/ui/button'
import { TextLink } from '@/shared/ui/page'

/**
 * Chave do "dispensar" no cache de consultas, por conta: sobrevive à troca de rota (as áreas remontam a cada
 * navegação) e some com a troca de identidade, que limpa o cache. Nada vai para o armazenamento do navegador.
 */
function dismissedKey(userId: string): readonly unknown[] {
  return ['account', userId, 'terms-reacceptance-notice-dismissed']
}

/**
 * Aviso não bloqueante de novo aceite dos Termos (issue #296): aparece em toda área com sessão enquanto a conta não
 * aceitou a versão ativa, leva à tela de novo aceite e pode ser dispensado até a próxima visita. O app continua
 * utilizável com ou sem o aceite.
 */
export function TermsReacceptanceNotice(): React.JSX.Element | null {
  const account = useAccount()
  const { t } = useI18n()
  const { pathname } = useLocation()
  const queries = useQueryClient()
  const headingId = useId()
  const region = useRef<HTMLElement>(null)
  const dismissed = useQuery({
    queryKey: dismissedKey(account.id),
    queryFn: () => false,
    staleTime: Infinity,
    gcTime: Infinity,
  })
  if (account.currentTermsAccepted || pathname === paths.termsReacceptance || dismissed.data === true) return null
  return (
    <aside
      ref={region}
      aria-labelledby={headingId}
      className="mb-8 rounded-md border-l-4 border-action bg-surface-muted px-4 py-3 text-text"
    >
      {/* Texto em destaque, não h2: o aviso vem antes do h1 da página e não entra na hierarquia de títulos. */}
      <p id={headingId} className="font-semibold">
        {t('termsReacceptance.noticeTitle')}
      </p>
      <p className="mt-1">{t('termsReacceptance.noticeBody')}</p>
      <div className="mt-2 flex flex-wrap items-center gap-x-6 gap-y-2">
        <TextLink to={paths.termsReacceptance}>{t('termsReacceptance.noticeAction')}</TextLink>
        <Button
          variant="neutral"
          onClick={() => {
            // O botão vai sumir: o foco passa ao conteúdo principal em vez de cair no body.
            region.current?.closest('main')?.focus()
            queries.setQueryData(dismissedKey(account.id), true)
          }}
        >
          {t('termsReacceptance.noticeDismiss')}
        </Button>
      </div>
    </aside>
  )
}
