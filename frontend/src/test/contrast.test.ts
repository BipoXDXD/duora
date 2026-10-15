// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { contrastRatio, isInSrgbGamut, oklchToLinearRgb, parseOklch } from './contrast'

describe('contraste a partir de OKLCH', () => {
  it('branco contra preto dá 21:1', () => {
    expect(contrastRatio(parseOklch('oklch(1 0 0)'), parseOklch('oklch(0 0 0)'))).toBeCloseTo(21, 2)
  })

  it('a ordem dos argumentos não altera o resultado', () => {
    const grey = parseOklch('oklch(0.6 0 0)')
    const white = parseOklch('oklch(1 0 0)')
    expect(contrastRatio(grey, white)).toBeCloseTo(contrastRatio(white, grey), 10)
  })

  it('cinza acromático de L 0.6 tem luminância 0.6³ e contraste de 3,95:1 contra branco', () => {
    // Cálculo à mão: (1 + 0.05) / (0.216 + 0.05) = 3.947.
    expect(contrastRatio(parseOklch('oklch(0.6 0 0)'), parseOklch('oklch(1 0 0)'))).toBeCloseTo(3.947, 3)
  })

  it('detecta cor fora do gamut sRGB', () => {
    expect(isInSrgbGamut(oklchToLinearRgb(parseOklch('oklch(0.5 0.4 145)')))).toBe(false)
    expect(isInSrgbGamut(oklchToLinearRgb(parseOklch('oklch(0.5 0.1 145)')))).toBe(true)
  })

  it('recusa valor fora do formato oklch(L C H)', () => {
    expect(() => parseOklch('#ffffff')).toThrow(/oklch/)
  })
})
