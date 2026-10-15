import type { ComponentProps } from 'react'
import { cva, type VariantProps } from 'class-variance-authority'
import { cn } from 'cn'
import { Slot } from 'radix-ui'

// Base do shadcn/ui (estilo new-york) adaptada aos tokens do Duora: alvo de 44 px (spec 13.4),
// anel de foco sólido em --color-focus (3:1 medido) e as quatro ações da spec 13.3.
// O estado desabilitado usa tokens com contraste medido em vez de opacidade.
const buttonVariants = cva(
  [
    'inline-flex min-h-11 shrink-0 items-center justify-center gap-2 rounded-md px-4 py-2',
    'text-base font-semibold whitespace-nowrap transition-colors',
    'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus',
    'disabled:cursor-not-allowed disabled:border-border disabled:bg-surface-muted disabled:text-text-muted',
    '[&_svg]:pointer-events-none [&_svg]:size-4 [&_svg]:shrink-0',
  ],
  {
    variants: {
      variant: {
        primary: 'bg-action text-action-foreground hover:bg-action-hover',
        secondary: 'border border-action bg-surface text-action hover:bg-surface-muted',
        destructive: 'bg-danger text-danger-foreground hover:bg-danger-hover',
        neutral: 'text-text underline-offset-4 hover:bg-surface-muted hover:underline',
      },
    },
    defaultVariants: {
      variant: 'primary',
    },
  },
)

type ButtonProps = ComponentProps<'button'> &
  VariantProps<typeof buttonVariants> & {
    /** Repassa estilo e comportamento ao filho único (por exemplo, um link de rota). */
    asChild?: boolean
  }

function Button({ className, variant, asChild = false, type, ...props }: ButtonProps): React.JSX.Element {
  const classes = cn(buttonVariants({ variant, className }))

  if (asChild) {
    return <Slot.Root data-slot="button" className={classes} {...props} />
  }
  return <button data-slot="button" type={type ?? 'button'} className={classes} {...props} />
}

export { Button }
