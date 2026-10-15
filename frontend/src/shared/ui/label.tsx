import type { ComponentProps } from 'react'
import { cn } from 'cn'
import { Label as LabelPrimitive } from 'radix-ui'

// Label do shadcn/ui sobre a primitiva Radix: sempre visível acima do campo (spec 13.4, A11Y-01).
function Label({ className, ...props }: ComponentProps<typeof LabelPrimitive.Root>): React.JSX.Element {
  return <LabelPrimitive.Root data-slot="label" className={cn('font-semibold text-text', className)} {...props} />
}

export { Label }
