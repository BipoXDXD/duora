import type { ReactNode } from 'react'
import { cn } from 'cn'
import type { RequestFailure } from '@/shared/api'
import { messages } from '@/shared/messages'

type AlertVariant = 'error' | 'info' | 'success'

interface AlertStyle {
  readonly role: 'alert' | 'status'
  readonly title: string
  readonly className: string
}

// Erro interrompe o leitor de tela (role=alert); aviso e sucesso esperam a vez (role=status).
// A variante nunca depende só da cor: cada uma tem título em texto e borda de acento.
const ALERT_STYLES: Readonly<Record<AlertVariant, AlertStyle>> = {
  error: { role: 'alert', title: messages.common.errorTitle, className: 'border-danger' },
  info: { role: 'status', title: messages.common.noticeTitle, className: 'border-action' },
  success: { role: 'status', title: messages.common.successTitle, className: 'border-action' },
}

interface AlertProps {
  readonly variant: AlertVariant
  readonly children: ReactNode
  readonly className?: string
}

export function Alert({ variant, children, className }: AlertProps): React.JSX.Element {
  const style = ALERT_STYLES[variant]
  return (
    <div
      role={style.role}
      className={cn('rounded-md border-l-4 bg-surface-muted px-4 py-3 text-text', style.className, className)}
    >
      <p className="font-semibold">{style.title}</p>
      <div className="mt-1">{children}</div>
    </div>
  )
}

interface RequestFailureAlertProps {
  readonly failure: RequestFailure
  /** Mensagem por `code` do Problem Details que esta tela sabe explicar. */
  readonly problemMessages?: Readonly<Partial<Record<string, string>>>
}

/** Mensagem de falha de uma requisição: rede, limite de uso, code conhecido ou genérica. */
export function RequestFailureAlert({ failure, problemMessages = {} }: RequestFailureAlertProps): React.JSX.Element {
  return <Alert variant="error">{describeFailure(failure, problemMessages)}</Alert>
}

function describeFailure(failure: RequestFailure, problemMessages: Readonly<Partial<Record<string, string>>>): string {
  switch (failure.kind) {
    case 'network':
      return messages.common.networkError
    case 'rate-limited':
      return messages.common.rateLimited(failure.retryAfterSeconds)
    case 'problem':
      return (failure.code === null ? undefined : problemMessages[failure.code]) ?? messages.common.unexpectedError
    case 'unexpected':
      return messages.common.unexpectedError
  }
}
