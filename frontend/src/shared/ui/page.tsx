import { useId, type ReactNode } from 'react'
import { cn } from 'cn'
import { Link, type LinkProps } from 'react-router'
import { Button } from './button'
import { DocumentTitle } from './document-title'

interface PageProps {
  /** Título da rota: vira o h1 e o título do documento (WCAG 2.4.2, A11Y-02). */
  readonly title: string
  readonly intro?: string
  /** Ações do cabeçalho da página, alinhadas ao título. */
  readonly actions?: ReactNode
  /** `narrow` para formulários; `wide` para áreas com listas. */
  readonly width?: 'narrow' | 'wide'
  readonly children: ReactNode
}

export function Page({ title, intro, actions, width = 'narrow', children }: PageProps): React.JSX.Element {
  const headingId = useId()
  return (
    <section aria-labelledby={headingId} className={cn('w-full', width === 'narrow' ? 'max-w-md' : 'max-w-2xl')}>
      <DocumentTitle page={title} />
      <div className="flex flex-wrap items-baseline justify-between gap-4">
        <h1 id={headingId} className="text-3xl font-semibold tracking-tight">
          {title}
        </h1>
        {actions}
      </div>
      {intro === undefined ? null : <p className="mt-4 text-text-muted">{intro}</p>}
      <div className="mt-8 flex flex-col gap-6">{children}</div>
    </section>
  )
}

/** Link de texto com sublinhado permanente, que sobrevive a toque e teclado (sem depender de hover). */
export function TextLink({ className, ...props }: LinkProps): React.JSX.Element {
  return (
    <Link
      className={cn(
        'inline-flex min-h-11 items-center font-semibold text-action underline underline-offset-4',
        'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus',
        className,
      )}
      {...props}
    />
  )
}

interface SubmitButtonProps {
  readonly pending: boolean
  readonly label: string
  readonly pendingLabel: string
}

/**
 * Botão de envio que continua focável durante o envio (aria-disabled em vez de disabled, para o
 * foco não cair no body); o formulário ignora novos envios enquanto `pending`.
 */
export function SubmitButton({ pending, label, pendingLabel }: SubmitButtonProps): React.JSX.Element {
  return (
    <Button type="submit" aria-disabled={pending} className="w-full sm:w-auto">
      {pending ? pendingLabel : label}
    </Button>
  )
}
