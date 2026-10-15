/**
 * Contraste WCAG 2.x a partir de cores OKLCH, usado só nos testes de tokens.
 * Conversão OKLCH → OKLab → sRGB linear pelas matrizes publicadas por Björn Ottosson.
 */
export interface Oklch {
  readonly lightness: number
  readonly chroma: number
  readonly hueDegrees: number
}

export interface LinearRgb {
  readonly r: number
  readonly g: number
  readonly b: number
}

const GAMUT_TOLERANCE = 1e-4

export function parseOklch(value: string): Oklch {
  const match = /^oklch\(\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)\s*\)$/.exec(value.trim())
  if (match === null) {
    throw new Error(`Cor fora do formato oklch(L C H): "${value}"`)
  }
  const [, lightness = '', chroma = '', hue = ''] = match
  return { lightness: Number(lightness), chroma: Number(chroma), hueDegrees: Number(hue) }
}

export function oklchToLinearRgb({ lightness, chroma, hueDegrees }: Oklch): LinearRgb {
  const hue = (hueDegrees * Math.PI) / 180
  const a = chroma * Math.cos(hue)
  const b = chroma * Math.sin(hue)

  const l = (lightness + 0.3963377774 * a + 0.2158037573 * b) ** 3
  const m = (lightness - 0.1055613458 * a - 0.0638541728 * b) ** 3
  const s = (lightness - 0.0894841775 * a - 1.291485548 * b) ** 3

  return {
    r: 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    g: -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    b: -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s,
  }
}

export function isInSrgbGamut({ r, g, b }: LinearRgb): boolean {
  return [r, g, b].every((channel) => channel >= -GAMUT_TOLERANCE && channel <= 1 + GAMUT_TOLERANCE)
}

export function contrastRatio(foreground: Oklch, background: Oklch): number {
  const lighter = Math.max(relativeLuminance(foreground), relativeLuminance(background))
  const darker = Math.min(relativeLuminance(foreground), relativeLuminance(background))
  return (lighter + 0.05) / (darker + 0.05)
}

function relativeLuminance(color: Oklch): number {
  const { r, g, b } = oklchToLinearRgb(color)
  return 0.2126 * clampUnit(r) + 0.7152 * clampUnit(g) + 0.0722 * clampUnit(b)
}

function clampUnit(channel: number): number {
  return Math.min(1, Math.max(0, channel))
}
