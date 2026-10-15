// @vitest-environment node
import { describe, expect, it } from 'vitest'
import stylesheet from '@/index.css?raw'
import { contrastRatio, isInSrgbGamut, oklchToLinearRgb, parseOklch, type Oklch } from '@/test/contrast'

const TEXT_MINIMUM = 4.5 // WCAG 1.4.3, texto normal
const NON_TEXT_MINIMUM = 3 // WCAG 1.4.11, componentes e indicador de foco

function readColorTokens(css: string): ReadonlyMap<string, Oklch> {
  const tokens = new Map<string, Oklch>()
  for (const [, name = '', value = ''] of css.matchAll(/--color-([a-z-]+):\s*(oklch\([^)]*\))/g)) {
    tokens.set(name, parseOklch(value))
  }
  return tokens
}

const tokens = readColorTokens(stylesheet)

function token(name: string): Oklch {
  const value = tokens.get(name)
  if (value === undefined) {
    throw new Error(`Token --color-${name} não definido em src/index.css`)
  }
  return value
}

describe('tokens de cor', () => {
  it.each(['surface', 'surface-muted', 'text', 'text-muted', 'action', 'danger', 'focus'])(
    'define o token semântico %s',
    (name) => {
      expect(tokens.has(name)).toBe(true)
    },
  )

  it.each([...tokens.keys()])('%s está dentro do gamut sRGB', (name) => {
    expect(isInSrgbGamut(oklchToLinearRgb(token(name)))).toBe(true)
  })

  it.each([
    ['text', 'surface'],
    ['text', 'surface-muted'],
    ['text-muted', 'surface'],
    ['text-muted', 'surface-muted'],
    ['action', 'surface'],
    ['danger', 'surface'],
    ['action-foreground', 'action'],
    ['action-foreground', 'action-hover'],
    ['danger-foreground', 'danger'],
    ['danger-foreground', 'danger-hover'],
  ])('texto %s sobre %s atinge 4,5:1', (foreground, background) => {
    expect(contrastRatio(token(foreground), token(background))).toBeGreaterThanOrEqual(TEXT_MINIMUM)
  })

  it.each([
    ['focus', 'surface'],
    ['focus', 'surface-muted'],
    ['border', 'surface'],
    ['border', 'surface-muted'],
  ])('elemento não textual %s sobre %s atinge 3:1', (foreground, background) => {
    expect(contrastRatio(token(foreground), token(background))).toBeGreaterThanOrEqual(NON_TEXT_MINIMUM)
  })
})
