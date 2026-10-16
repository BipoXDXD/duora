import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { z } from 'zod'
import catalogJson from '../../../../tools/visual/catalog.json?raw'
import licensesMarkdown from '../../../../docs/design/assets-licenses.md?raw'
import { Illustration } from './illustration'
import { ILLUSTRATIONS } from './illustrations.generated'

const catalogSchema = z.object({
  uses: z.record(z.string(), z.object({ cssPx: z.number().int().positive() })),
  objects: z.array(z.object({ name: z.string(), source: z.enum(['generated', 'procedural']), use: z.string() })),
})
const catalog = catalogSchema.parse(JSON.parse(catalogJson))
const UUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/

describe('Illustration', () => {
  it('descreve a imagem com o alt informado e reserva o tamanho do uso para não deslocar o layout', () => {
    render(<Illustration name="compass" alt="Bússola aberta" />)

    const image = screen.getByRole('img', { name: 'Bússola aberta' })
    expect(image).toHaveAttribute('width', '200')
    expect(image).toHaveAttribute('height', '200')
    expect(image).not.toHaveAttribute('aria-hidden')
  })

  it('oferece 1x e 2x no srcset e carrega sob demanda', () => {
    render(<Illustration name="envelope" alt="Envelope fechado" />)

    const image = screen.getByRole('img', { name: 'Envelope fechado' })
    const { src1x, src2x } = ILLUSTRATIONS.envelope
    expect(image).toHaveAttribute('src', src1x)
    expect(image).toHaveAttribute('srcset', `${src1x} 1x, ${src2x} 2x`)
    expect(image).toHaveAttribute('loading', 'lazy')
    expect(image).toHaveAttribute('decoding', 'async')
  })

  it('decorativa tem alt vazio e fica fora da árvore de acessibilidade', () => {
    const { container } = render(<Illustration name="lantern" decorative />)

    const image = container.querySelector('img')
    expect(image).toHaveAttribute('alt', '')
    expect(image).toHaveAttribute('aria-hidden', 'true')
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
  })

  it.each(['', '   '])('recusa alt em branco (%j) sem decorative explícito', (alt) => {
    expect(() => render(<Illustration name="shield" alt={alt} />)).toThrow(/decorative/)
  })

  it('aceita classes extras sem perder as de tamanho responsivo', () => {
    render(<Illustration name="padlock" alt="Cadeado" className="mx-auto" />)

    expect(screen.getByRole('img', { name: 'Cadeado' })).toHaveClass('mx-auto', 'h-auto', 'max-w-full')
  })

  it('só aceita nomes do catálogo e exige alt ou decorative', () => {
    // @ts-expect-error nome fora do catálogo gerado
    const unknownName = <Illustration name="rocket" alt="Foguete" />
    // @ts-expect-error sem alt nem decorative
    const missingAlt = <Illustration name="compass" />
    // @ts-expect-error alt e decorative juntos são contraditórios
    const both = <Illustration name="compass" alt="Bússola" decorative />

    expect([unknownName, missingAlt, both]).toHaveLength(3)
  })
})

describe('registro de ilustrações', () => {
  it('o manifesto gerado tem exatamente os objetos do catálogo, com o cssPx do uso', () => {
    const expected = Object.fromEntries(catalog.objects.map((item) => [item.name, catalog.uses[item.use]?.cssPx]))
    const actual = Object.fromEntries(Object.entries(ILLUSTRATIONS).map(([name, asset]) => [name, asset.cssPx]))

    expect(actual).toEqual(expected)
  })

  it.each(catalog.objects.filter((item) => item.source === 'generated').map((item) => item.name))(
    '%s tem linha no registro de licenças com o task id da geração',
    (name) => {
      const row = licensesMarkdown.split('\n').find((line) => line.startsWith(`| \`${name}\` |`))

      expect(row).toMatch(UUID)
    },
  )
})
