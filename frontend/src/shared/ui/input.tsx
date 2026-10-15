import type { ComponentProps } from 'react'
import { cn } from 'cn'

// Base do Input do shadcn/ui (new-york) sobre os tokens do Duora: alvo de 44 px, corpo de 16 px
// (evita o zoom automático do iOS), borda com 3:1 medido e anel de foco sólido em --color-focus.
function Input({ className, type, ...props }: ComponentProps<'input'>): React.JSX.Element {
  return (
    <input
      data-slot="input"
      type={type}
      className={cn(
        'min-h-11 w-full min-w-0 rounded-md border border-border bg-surface px-3 py-2 text-base text-text',
        'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus',
        'aria-invalid:border-2 aria-invalid:border-danger',
        'disabled:cursor-not-allowed disabled:bg-surface-muted disabled:text-text-muted',
        className,
      )}
      {...props}
    />
  )
}

export { Input }
