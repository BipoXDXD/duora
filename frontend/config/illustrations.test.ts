// @vitest-environment node
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { build, type Rollup } from 'vite'
import { describe, expect, it } from 'vitest'
import { z } from 'zod'

// Registro das ilustrações: o manifesto gerado, o catálogo de tools/visual e o registro de licenças precisam
// concordar, e o build não pode embutir nenhum WebP como data: (a CSP img-src 'self' do Caddy o bloquearia).

const MANIFEST_URL = new URL('../src/shared/ui/illustrations.generated.ts', import.meta.url)

const catalogSchema = z.object({
  promptPrefix: z.string(),
  uses: z.record(z.string(), z.object({ cssPx: z.number().int().positive() })),
  objects: z.array(z.object({ name: z.string(), source: z.enum(['generated', 'procedural']), use: z.string() })),
})

const catalog = catalogSchema.parse(
  JSON.parse(readFileSync(new URL('../../tools/visual/catalog.json', import.meta.url), 'utf8')),
)
const generated = catalog.objects.filter((item) => item.source === 'generated')
const licenses = readFileSync(new URL('../../docs/design/assets-licenses.md', import.meta.url), 'utf8')
const manifest = readFileSync(MANIFEST_URL, 'utf8')

const MANIFEST_ENTRY = /^ {2}'?([a-z0-9-]+)'?: \{ cssPx: (\d+),/gm
const UUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/
// Texto vem do catálogo de idiomas, nunca da imagem. Pessoas e personagens são permitidos (ADR-0027).
const FORBIDDEN_IN_PROMPT = ['no text', 'no letters', 'no numbers', 'no symbols', 'no logos']
const BUILD_TIMEOUT_MS = 60_000

describe('catálogo de ilustrações', () => {
  it.each(FORBIDDEN_IN_PROMPT)('o prefixo de prompt proíbe "%s"', (term) => {
    expect(catalog.promptPrefix).toContain(term)
  })

  it('o registro de licenças cita o prefixo de prompt vigente do catálogo, sem divergir', () => {
    expect(licenses).toContain(`  > ${catalog.promptPrefix}\n`)
  })

  it('o manifesto gerado tem só os objetos gerados do catálogo, com o cssPx do uso', () => {
    const expected = Object.fromEntries(
      generated.map((item): [string, number | undefined] => [item.name, catalog.uses[item.use]?.cssPx]),
    )
    const actual = Object.fromEntries(
      [...manifest.matchAll(MANIFEST_ENTRY)].map(([, name, px]): [string, number] => [String(name), Number(px)]),
    )

    expect(actual).toEqual(expected)
  })

  it.each(generated.map((item) => item.name))('%s tem linha no registro de licenças com o task id', (name) => {
    const row = licenses.split('\n').find((line) => line.startsWith(`| \`${name}\` |`))

    expect(row).toMatch(UUID)
  })
})

describe('build das ilustrações', () => {
  it(
    'emite cada WebP como arquivo e nenhum como data:',
    async () => {
      const output = await buildManifest()

      const webpFiles = output.filter((item) => item.type === 'asset' && item.fileName.endsWith('.webp'))
      const code = output.flatMap((item) => (item.type === 'chunk' ? [item.code] : [])).join('\n')
      expect(webpFiles).toHaveLength(generated.length * 2)
      expect(code).not.toContain('data:')
    },
    BUILD_TIMEOUT_MS,
  )
})

async function buildManifest(): Promise<readonly (Rollup.OutputAsset | Rollup.OutputChunk)[]> {
  const result = await build({
    configFile: false,
    root: fileURLToPath(new URL('..', import.meta.url)),
    logLevel: 'silent',
    build: { write: false, rolldownOptions: { input: fileURLToPath(MANIFEST_URL) } },
  })
  if (!('output' in result)) {
    throw new Error('build em modo watch não era esperado')
  }
  return result.output
}
