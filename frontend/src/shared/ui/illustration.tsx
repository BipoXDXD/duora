import { cn } from 'cn'
import { ILLUSTRATIONS } from './illustrations.generated'

/** Nomes de tools/visual/catalog.json, via o manifesto gerado por tools/visual/process.py. */
export type IllustrationName = keyof typeof ILLUSTRATIONS

/**
 * `alt` descreve o que a imagem acrescenta ao texto ao lado; quando ela só enfeita, `decorative` deixa isso
 * explícito em vez de um `alt=""` esquecido.
 */
type IllustrationProps = {
  readonly name: IllustrationName
  readonly className?: string
} & ({ readonly alt: string; readonly decorative?: never } | { readonly decorative: true; readonly alt?: never })

/** Ilustração estática do catálogo, em 1x e 2x, com largura e altura fixas para não deslocar o layout. */
export function Illustration(props: IllustrationProps): React.JSX.Element {
  const { cssPx, src1x, src2x } = ILLUSTRATIONS[props.name]
  const alt = props.decorative === true ? '' : props.alt
  if (props.decorative !== true && alt.trim() === '') {
    throw new Error(`Illustration "${props.name}" sem alt: descreva a imagem ou marque decorative.`)
  }
  return (
    <img
      src={src1x}
      srcSet={`${src1x} 1x, ${src2x} 2x`}
      width={cssPx}
      height={cssPx}
      alt={alt}
      aria-hidden={props.decorative === true ? true : undefined}
      loading="lazy"
      decoding="async"
      className={cn('h-auto max-w-full', props.className)}
    />
  )
}
