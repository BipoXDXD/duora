import { useEffect, useRef, type ReactNode } from 'react'
import { cn } from 'cn'
import type { RequestFailure } from '@/shared/api'
import type { PlainMessageKey } from '@/shared/i18n/catalog'
import { useI18n } from '@/shared/i18n/i18n-context'
import type { Translate } from '@/shared/i18n/translator'
import { retryWait } from './retry-wait'

type AlertVariant = 'error' | 'info' | 'success'

interface AlertStyle {
  readonly role: 'alert' | 'status'
  readonly title: PlainMessageKey
  readonly className: string
}

// Erro interrompe o leitor de tela (role=alert); aviso e sucesso esperam a vez (role=status).
// A variante nunca depende só da cor: cada uma tem título em texto e borda de acento.
const ALERT_STYLES: Readonly<Record<AlertVariant, AlertStyle>> = {
  error: { role: 'alert', title: 'common.errorTitle', className: 'border-danger' },
  info: { role: 'status', title: 'common.noticeTitle', className: 'border-action' },
  success: { role: 'status', title: 'common.successTitle', className: 'border-action' },
}

interface AlertProps {
  readonly variant: AlertVariant
  readonly children: ReactNode
  readonly className?: string
  /**
   * Recebe o foco ao aparecer. Use quando o alerta substitui o controle que estava focado (o
   * formulário some depois do envio): sem isso, o foco cai no body.
   */
  readonly focusOnMount?: boolean
}

export function Alert({ variant, children, className, focusOnMount = false }: AlertProps): React.JSX.Element {
  const style = ALERT_STYLES[variant]
  const { t } = useI18n()
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (focusOnMount) {
      ref.current?.focus()
    }
  }, [focusOnMount])
  return (
    <div
      ref={ref}
      tabIndex={focusOnMount ? -1 : undefined}
      role={style.role}
      className={cn('rounded-md border-l-4 bg-surface-muted px-4 py-3 text-text', style.className, className)}
    >
      <p className="font-semibold">{t(style.title)}</p>
      <div className="mt-1">{children}</div>
    </div>
  )
}

interface RequestFailureAlertProps {
  readonly failure: RequestFailure
  /** Chave da mensagem por `code` do Problem Details que esta tela sabe explicar. */
  readonly problemMessages?: ProblemMessages
}

type ProblemMessages = Readonly<Partial<Record<string, PlainMessageKey>>>

/** Mensagem de falha de uma requisição: rede, limite de uso, code conhecido ou genérica. */
export function RequestFailureAlert({ failure, problemMessages = {} }: RequestFailureAlertProps): React.JSX.Element {
  const { t } = useI18n()
  return <Alert variant="error">{describeFailure(t, failure, problemMessages)}</Alert>
}

function describeFailure(t: Translate, failure: RequestFailure, problemMessages: ProblemMessages): string {
  switch (failure.kind) {
    case 'network':
      return t('common.networkError')
    case 'rate-limited':
      return t('common.rateLimited', retryWait(failure.retryAfterSeconds))
    case 'problem':
      return t((failure.code === null ? undefined : problemMessages[failure.code]) ?? 'common.unexpectedError')
    case 'unexpected':
      return t('common.unexpectedError')
  }
}
